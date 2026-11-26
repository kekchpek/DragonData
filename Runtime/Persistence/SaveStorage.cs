using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Kekchpek.DragonData
{
    internal sealed class SaveStorage
    {
        private const uint Signature = 0x01534444; // DDS, format version 1.
        private const int ChecksumSize = 32;
        private const int BufferSize = 64 * 1024;
        private const int MaxNameBytes = 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly char[] InvalidNameCharacters = "<>:\"/\\|?*".ToCharArray();
        private static readonly object ReplaceLock = new object();

        private readonly string _directoryPath;

        internal SaveStorage(string directoryPath)
        {
            _directoryPath = Path.GetFullPath(directoryPath);
        }

        internal static string DefaultDirectory => Path.Combine(Application.persistentDataPath, "GameSaves");

        internal void Save(string containerName, string saveName, Action<Stream> write)
        {
            var path = GetPath(containerName, saveName);
            Directory.CreateDirectory(_directoryPath);
            var temporaryPath = Path.Combine(_directoryPath, $".{Guid.NewGuid():N}.ddsave.tmp");
            bool ownsTemporaryFile = false;

            try
            {
                using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporaryFile = true;
                    using var output = new PayloadWriter(file);
                    WriteIdentity(output, containerName, saveName);
                    output.BeginPayload();
                    write(output);
                    output.WriteChecksum();
                    file.Flush(true);
                }

                using (var file = File.OpenRead(temporaryPath))
                {
                    ValidateChecksum(file);
                }

                lock (ReplaceLock)
                {
                    bool replace;
                    using (var existing = OpenReadIfExists(path))
                    {
                        replace = existing != null;
                        if (replace)
                        {
                            EnsureSameIdentityOrInvalid(existing, containerName, saveName);
                        }
                    }

                    if (replace)
                    {
                        File.Replace(temporaryPath, path, null);
                    }
                    else
                    {
                        File.Move(temporaryPath, path);
                    }
                }
            }
            catch
            {
                if (ownsTemporaryFile)
                {
                    RemoveTemporaryFile(temporaryPath);
                }

                throw;
            }
        }

        internal bool Load(string containerName, string saveName, Action<Stream> read)
        {
            using var file = OpenReadIfExists(GetPath(containerName, saveName));
            if (file == null)
            {
                return false;
            }

            long checksumPosition = ValidateChecksum(file);
            if (!TryReadIdentity(file, checksumPosition, out var storedContainer, out var storedSave))
            {
                throw new InvalidDataException("Invalid DragonData save header.");
            }

            if (storedContainer != containerName || storedSave != saveName)
            {
                throw new InvalidDataException("The save file belongs to a different container or save name.");
            }

            using var payload = new PayloadReader(file, checksumPosition);
            read(payload);
            return true;
        }

        internal static void ValidateName(string name, string parameterName)
        {
            if (name == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(InvalidNameCharacters) >= 0)
            {
                throw new ArgumentException("Use a non-empty file name without path separators or reserved characters.", parameterName);
            }

            foreach (char character in name)
            {
                if (character < ' ')
                {
                    throw new ArgumentException("File names must not contain control characters.", parameterName);
                }
            }

            if (Utf8.GetByteCount(name) > MaxNameBytes)
            {
                throw new ArgumentException("The name is too long for the save header.", parameterName);
            }
        }

        private string GetPath(string containerName, string saveName)
        {
            ValidateName(containerName, nameof(containerName));
            ValidateName(saveName, nameof(saveName));
            return Path.Combine(_directoryPath, $"{containerName}_{saveName}.ddsave");
        }

        private static FileStream OpenReadIfExists(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        private static void EnsureSameIdentityOrInvalid(FileStream file, string containerName, string saveName)
        {
            if (!TryReadIdentity(file, file.Length - ChecksumSize, out var storedContainer, out var storedSave) ||
                (storedContainer == containerName && storedSave == saveName))
            {
                return;
            }

            file.Position = 0;
            try
            {
                ValidateChecksum(file);
            }
            catch (InvalidDataException)
            {
                return;
            }

            throw new IOException("The save filename is already used by a different container or save name.");
        }

        private static void RemoveTemporaryFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Preserve the exception that prevented the save.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the exception that prevented the save.
            }
        }

        private static void WriteIdentity(Stream stream, string containerName, string saveName)
        {
            Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
            Span<byte> nameBytes = stackalloc byte[MaxNameBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, Signature);
            stream.Write(lengthBytes);
            WriteName(stream, containerName, lengthBytes, nameBytes);
            WriteName(stream, saveName, lengthBytes, nameBytes);
        }

        private static void WriteName(Stream stream, string name, Span<byte> lengthBytes, Span<byte> nameBytes)
        {
            int byteCount = Utf8.GetBytes(name.AsSpan(), nameBytes);
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, byteCount);
            stream.Write(lengthBytes);
            stream.Write(nameBytes.Slice(0, byteCount));
        }

        private static bool TryReadIdentity(Stream stream, long checksumPosition, out string containerName, out string saveName)
        {
            containerName = null;
            saveName = null;
            Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
            Span<byte> nameBytes = stackalloc byte[MaxNameBytes];

            return checksumPosition - stream.Position >= lengthBytes.Length &&
                   TryReadExactly(stream, lengthBytes) &&
                   BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes) == Signature &&
                   TryReadName(stream, checksumPosition, lengthBytes, nameBytes, out containerName) &&
                   TryReadName(stream, checksumPosition, lengthBytes, nameBytes, out saveName);
        }

        private static bool TryReadName(
            Stream stream, long checksumPosition, Span<byte> lengthBytes, Span<byte> nameBytes, out string name)
        {
            name = null;
            if (checksumPosition - stream.Position < lengthBytes.Length || !TryReadExactly(stream, lengthBytes))
            {
                return false;
            }

            int byteCount = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (byteCount <= 0 || byteCount > nameBytes.Length || byteCount > checksumPosition - stream.Position)
            {
                return false;
            }

            var encodedName = nameBytes.Slice(0, byteCount);
            if (!TryReadExactly(stream, encodedName))
            {
                return false;
            }

            try
            {
                name = Utf8.GetString(encodedName);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static long ValidateChecksum(Stream stream)
        {
            long checksumPosition = stream.Length - ChecksumSize;
            if (checksumPosition < 0)
            {
                throw new InvalidDataException("The save file is missing its checksum.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = checksumPosition;
            if (remaining > 0)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (remaining > 0)
                    {
                        int count = stream.Read(buffer, 0, (int)Math.Min(remaining, BufferSize));
                        if (count == 0)
                        {
                            throw new InvalidDataException("The save file ended before its checksum.");
                        }

                        hash.AppendData(buffer, 0, count);
                        remaining -= count;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            Span<byte> digest = stackalloc byte[ChecksumSize];
            Span<byte> stored = stackalloc byte[ChecksumSize];
            if (!hash.TryGetHashAndReset(digest, out _) ||
                !TryReadExactly(stream, stored) ||
                !CryptographicOperations.FixedTimeEquals(digest, stored))
            {
                throw new InvalidDataException("The save checksum does not match its contents.");
            }

            stream.Position = 0;
            return checksumPosition;
        }

        private static bool TryReadExactly(Stream stream, Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                int count = stream.Read(destination);
                if (count == 0)
                {
                    return false;
                }

                destination = destination.Slice(count);
            }

            return true;
        }

        private sealed class PayloadWriter : Stream
        {
            private readonly FileStream _file;
            private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            private int _bufferCount;
            private long _position;

            internal PayloadWriter(FileStream file)
            {
                _file = file;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => _buffer != null;
            public override long Length => _position;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            internal void BeginPayload() => _position = 0;

            internal void WriteChecksum()
            {
                FlushBuffer();
                Span<byte> digest = stackalloc byte[ChecksumSize];
                if (!_hash.TryGetHashAndReset(digest, out _))
                {
                    throw new InvalidOperationException("Unable to compute the save checksum.");
                }

                _file.Write(digest);
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> source)
            {
                // The Unity Mono FileStream span overload otherwise rents a buffer as large as the span.
                _position += source.Length;
                while (!source.IsEmpty)
                {
                    if (_bufferCount == BufferSize)
                    {
                        FlushBuffer();
                    }

                    int count = Math.Min(source.Length, BufferSize - _bufferCount);
                    source.Slice(0, count).CopyTo(_buffer.AsSpan(_bufferCount));
                    _bufferCount += count;
                    source = source.Slice(count);
                }
            }

            public override void WriteByte(byte value)
            {
                if (_bufferCount == BufferSize)
                {
                    FlushBuffer();
                }

                _buffer[_bufferCount++] = value;
                _position++;
            }

            public override void Flush()
            {
                FlushBuffer();
                _file.Flush();
            }

            private void FlushBuffer()
            {
                if (_bufferCount > 0)
                {
                    _file.Write(_buffer, 0, _bufferCount);
                    _hash.AppendData(_buffer, 0, _bufferCount);
                    _bufferCount = 0;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null;
                    _hash.Dispose();
                }

                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class PayloadReader : Stream
        {
            private readonly FileStream _file;
            private readonly long _start;
            private long _position;

            internal PayloadReader(FileStream file, long end)
            {
                _file = file;
                _start = file.Position;
                Length = end - _start;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length { get; }

            public override long Position
            {
                get => _position;
                set
                {
                    if (value < 0 || value > Length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(value));
                    }

                    _file.Position = _start + value;
                    _position = value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var requested = buffer.AsSpan(offset, count);
                int read = _file.Read(buffer, offset, (int)Math.Min(Length - _position, requested.Length));
                _position += read;
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                int count = (int)Math.Min(Length - _position, Math.Min(buffer.Length, BufferSize));
                int read = _file.Read(buffer.Slice(0, count));
                _position += read;
                return read;
            }

            public override int ReadByte()
            {
                if (_position == Length)
                {
                    return -1;
                }

                int value = _file.ReadByte();
                if (value >= 0)
                {
                    _position++;
                }

                return value;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long start = origin switch
                {
                    SeekOrigin.Begin => 0,
                    SeekOrigin.Current => _position,
                    SeekOrigin.End => Length,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                Position = checked(start + offset);
                return _position;
            }

            public override void Flush() { }
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
