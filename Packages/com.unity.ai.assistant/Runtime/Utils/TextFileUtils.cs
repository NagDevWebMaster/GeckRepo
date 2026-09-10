using System.IO;
using System.Text;
using UnityEngine;

namespace Unity.AI.Assistant.Utils
{
    internal enum TextFileResult { Valid, Binary, HasBom }

    internal static class TextFileUtils
    {
        static readonly UTF8Encoding k_StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Heuristic to check if a file is binary or not
        public static bool IsTextFile(string path, int sampleSize = 4096)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);

                var len = (int)Mathf.Min(fs.Length, sampleSize);

                if (len == 0)
                    return true;

                var buffer = new byte[len];
                var readCount = fs.Read(buffer, 0, len);

                var nonPrintable = 0;
                for (var i = 0; i < readCount; i++)
                {
                    var b = buffer[i];

                    // Count the number of non printable characters
                    // Allow: tab (9), LF (10), CR (13), printable ASCII (32-126)
                    if (b != 9 && b != 10 && b != 13 && (b < 32 || b > 126))
                        nonPrintable++;
                }

                // If more than 5% of characters are non-printable, consider it binary
                var ratio = (float)nonPrintable / len;
                return ratio < 0.05f;
            }
            catch
            {
                return false;
            }
        }

        // Detects a UTF-8 BOM, rejects null bytes and ASCII control characters, and validates
        // UTF-8 sequences for files that fit entirely within the sample window.
        // Full validation of larger files is left to the caller's strict-decoder read.
        public static TextFileResult IsUtf8TextFile(string path, int sampleSize = 4096)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);

                var len = (int)Mathf.Min(fs.Length, sampleSize);

                if (len == 0)
                    return TextFileResult.Valid;

                var buffer = new byte[len];
                var readCount = fs.Read(buffer, 0, len);

                if (readCount >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                    return TextFileResult.HasBom;

                for (var i = 0; i < readCount; i++)
                {
                    var b = buffer[i];
                    if (b == 0 || (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D))
                        return TextFileResult.Binary;
                }

                // For files that fit entirely in the sample, validate the full byte sequence so
                // truncated multi-byte characters at EOF are caught. Larger files rely on the
                // caller's strict-decoder ReadAllText for anything beyond this window.
                if (fs.Length <= sampleSize)
                    k_StrictUtf8.GetCharCount(buffer, 0, readCount);

                return TextFileResult.Valid;
            }
            catch
            {
                return TextFileResult.Binary;
            }
        }
    }
}
