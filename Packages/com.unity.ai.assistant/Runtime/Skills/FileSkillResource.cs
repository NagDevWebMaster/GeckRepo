using System;
using System.IO;
using Unity.AI.Assistant.Utils;

namespace Unity.AI.Assistant.Skills
{
    /// <summary>
    /// A skill resource that loads content from a file on the filesystem.
    /// The content is loaded on-demand when GetContent() is called.
    /// </summary>
    class FileSkillResource : ISkillResource
    {
        readonly string m_FilePath;
        int? m_CachedSize;

        /// <summary>
        /// Creates a new file-based skill resource.
        /// </summary>
        /// <param name="filePath">Absolute path to the file</param>
        public FileSkillResource(string filePath)
        {
            m_FilePath = filePath;
        }

        public int Size
        {
            get
            {
                if (m_CachedSize.HasValue)
                    return m_CachedSize.Value;
                
                try
                {
                    if (string.IsNullOrEmpty(m_FilePath) || !File.Exists(m_FilePath))
                        m_CachedSize = 0;
                    else
                        m_CachedSize = (int)new FileInfo(m_FilePath).Length;
                }
                catch
                {
                    m_CachedSize = 0;
                }
                return m_CachedSize.Value;
            }
        }

        public int Length => Size;

        /// <summary>
        /// Loads and returns the content of the file.
        /// </summary>
        /// <returns>The file content as a string</returns>
        /// <exception cref="IOException">Thrown if the file cannot be read</exception>
        public string GetContent()
        {
            if (!File.Exists(m_FilePath))
                throw new FileNotFoundException($"Skill resource file doesn't exist at path: {m_FilePath}");
            
            var fileInfo = new FileInfo(m_FilePath);
            if (fileInfo.Length == 0)
            {
                throw new FileLoadException($"Skill resource file is empty: {m_FilePath}");
            }

            if (!TextFileUtils.IsTextFile(m_FilePath))
                throw new ArgumentException($"Skill resource '{m_FilePath}' does not appear to be a text file.");

            return File.ReadAllText(m_FilePath);
        }
    }
}
