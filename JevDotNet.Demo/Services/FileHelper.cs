using System.Reflection;
using System.Text.Json;
using System.Buffers.Binary;

namespace JevDotNet.Demo.Services
{
    public class FileHelper
    {
        public static T ExtractFromAssembly<T>(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = OpenResource(assembly, fileName);
            using var reader = new StreamReader(stream);
            object result = reader.ReadToEnd();
            if (typeof(T) == typeof(string))
                return (T)result;
            return JsonSerializer.Deserialize<T>(result.ToString()!)
                ?? throw new JsonException($"Embedded resource '{fileName}' contained no value.");
        }
        public static byte[] ExtractFileBytesFromAssembly(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = OpenResource(assembly, fileName);
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }
        public static string ExtractBase64FromAssembly(string fileName)
        {
            var bytes = ExtractFileBytesFromAssembly(fileName);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>Reads image bytes, excluding data appended after the PNG end marker.</summary>
        public static byte[] ExtractImageBytesFromAssembly(string fileName)
        {
            var bytes = ExtractFileBytesFromAssembly(fileName);
            if (!string.Equals(Path.GetExtension(fileName), ".png", StringComparison.OrdinalIgnoreCase))
                return bytes;

            if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException($"Embedded image '{fileName}' has an invalid PNG signature.");
            var offset = 8;
            while (offset <= bytes.Length - 12)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                if (length > bytes.Length - offset - 12)
                    throw new InvalidDataException($"Embedded image '{fileName}' has an incomplete PNG chunk.");
                var end = offset + (int)length + 12;
                if (bytes.AsSpan(offset + 4, 4).SequenceEqual("IEND"u8))
                {
                    if (length != 0)
                        throw new InvalidDataException($"Embedded image '{fileName}' has an invalid PNG end marker.");
                    // Keep every original PNG chunk, CRC, and pixel byte unchanged.
                    return end == bytes.Length ? bytes : bytes[..end];
                }
                offset = end;
            }
            throw new InvalidDataException($"Embedded image '{fileName}' is missing its PNG end marker.");
        }
        public static string? ExtractBase64ImageFromAssembly(string fileName)
        {
            var extention = Path.GetExtension(fileName).ToLowerInvariant();
            var mimeType = extention switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                _ => null
            };
            if (mimeType is null)
                return null;
            var bytes = ExtractImageBytesFromAssembly(fileName);
            return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
        }

        private static Stream OpenResource(Assembly assembly, string fileName)
        {
            var resourceName = assembly.GetManifestResourceNames()
                .SingleOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            return resourceName is null
                ? throw new FileNotFoundException($"Embedded resource '{fileName}' was not found.", fileName)
                : assembly.GetManifestResourceStream(resourceName)
                    ?? throw new FileNotFoundException($"Embedded resource '{fileName}' could not be opened.", fileName);
        }
    }
}
