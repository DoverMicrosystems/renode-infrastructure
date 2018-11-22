
using System.IO;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Storage
{
    public static class DataStorage
    {
        public static Stream Create(string imageFile, long? size = null, bool persistent = false)
        {
            if(string.IsNullOrEmpty(imageFile))
            {
                throw new ConstructionException("No image file provided.");
            }

            if(!persistent)
            {
                var tempFileName = TemporaryFilesManager.Instance.GetTemporaryFile();
                FileCopier.Copy(imageFile, tempFileName, true);
                imageFile = tempFileName;
            }

            return new SerializableFileStreamWrapper(imageFile, size).Stream;
        }
    }
}