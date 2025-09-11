using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public static class DirectoryTransferExtensions
{
    public static async Task<int> UploadDirectoryAsync(
        this SshSession session,
        string sourcePath,
        string targetPath,
        bool overwrite,
        bool recursive,
        Action<string>? writeInfo = null,
        Action<string>? writeError = null,
        Action<string>? writeWarning = null,
        Action<string>? writeSuccess = null)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(sourcePath, "*", searchOption).ToList();
        
        var uploadedFiles = 0;
        var failedFiles = new List<string>();

        var fileCountText = recursive 
            ? $"{files.Count} files (recursive)" 
            : $"{files.Count} files";
        writeInfo?.Invoke($"Uploading directory '{sourcePath}' ({fileCountText})...");

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(sourcePath, file)
                .Replace(Path.DirectorySeparatorChar, '/');

            try
            {
                await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read);
                var result = await session.UploadFileAsync(targetPath, relativePath, fileStream, overwrite, CancellationToken.None);
                
                if (result == 0)
                {
                    uploadedFiles++;
                    writeSuccess?.Invoke($"Uploaded: {Path.GetFileName(file)}");
                }
                else if (result == ErrorCodes.FileExists)
                {
                    writeWarning?.Invoke($"The file '{relativePath}' already exists at the destination and will not be overwritten.");
                }
                else
                {
                    failedFiles.Add(file);
                    writeWarning?.Invoke($"Failed to upload: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                failedFiles.Add(file);
                writeWarning?.Invoke($"Failed to upload {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (failedFiles.Count == 0)
        {
            writeSuccess?.Invoke($"Successfully uploaded {uploadedFiles} files to '{targetPath}'");
            return 0;
        }

        writeWarning?.Invoke($"Uploaded {uploadedFiles} files with {failedFiles.Count} failures:");
        foreach (var failedFile in failedFiles)
        {
            writeWarning?.Invoke($"Failed: {Path.GetFileName(failedFile)}");
        }
        return -1;
    }

    public static async Task<int> DownloadDirectoryAsync(
        this SshSession session, 
        string sourcePath, 
        string targetPath, 
        bool overwrite, 
        bool recursive,
        Action<string>? writeInfo = null,
        Action<string>? writeError = null,
        Action<string>? writeWarning = null,
        Action<string>? writeSuccess = null,
        CancellationToken cancellation = default)
    {
        List<RemoteFileInfo> files;
        try
        {
            var (listResult, filesList) = await session.ListDirectoryAsync(sourcePath, cancellation);
            if (listResult != 0)
            {
                writeError?.Invoke(listResult == ErrorCodes.FileNotFound
                    ? $"Directory '{sourcePath}' was not found on the VM."
                    : $"Failed to list directory '{sourcePath}' - Error code: {listResult}");
                return listResult;
            }
            
            files = filesList;
        }
        catch (Exception ex)
        {
            writeError?.Invoke($"Error listing directory '{sourcePath}': {ex.Message}");
            return -1;
        }

        // Ensure target directory exists (create if needed)
        Directory.CreateDirectory(targetPath);

        var downloadedFiles = 0;
        var failedFiles = new List<string>();

        // Count files in current directory
        var currentLevelFiles = files.Where(f => !f.IsDirectory).ToList();
        var fileCountText = recursive 
            ? $"directory (recursive - {currentLevelFiles.Count} files at root level)" 
            : $"directory ({currentLevelFiles.Count} files)";
        writeInfo?.Invoke($"Downloading {fileCountText} from '{sourcePath}'...");

        foreach (var file in files)
        {
            if (file.IsDirectory && recursive)
            {
                // Recursively download subdirectories (only if --recursive flag is set)
                var subDirSourcePath = file.FullPath;
                var subDirTargetPath = Path.Combine(targetPath, file.Name);
                
                var subDirResult = await DownloadDirectoryAsync(session, subDirSourcePath, subDirTargetPath, overwrite, recursive, writeInfo, writeError, writeWarning, writeSuccess, cancellation);
                if (subDirResult != 0)
                {
                    failedFiles.Add(subDirSourcePath);
                }
            }
            else if (!file.IsDirectory)
            {
                // Download individual file (only if it's not a directory)
                var targetFilePath = Path.Combine(targetPath, file.Name);
                
                // Create target directory for the file if it doesn't exist
                var fileDirectory = Path.GetDirectoryName(targetFilePath);
                if (!string.IsNullOrEmpty(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }

                try
                {
                    // Check if file exists and handle overwrite
                    if (File.Exists(targetFilePath) && !overwrite)
                    {
                        writeWarning?.Invoke($"Skipped: {file.Name} (already exists)");
                        continue;
                    }

                    await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
                    var result = await session.DownloadFileAsync(file.FullPath, targetStream, cancellation);
                    
                    if (result == 0)
                    {
                        downloadedFiles++;
                    }
                    else
                    {
                        failedFiles.Add(file.FullPath);
                        writeWarning?.Invoke(result == ErrorCodes.FileNotFound
                            ? $"File not found: {file.Name}"
                            : $"Failed to download: {file.Name} (Error: {result})");

                        // Clean up failed file
                        try
                        {
                            if (File.Exists(targetFilePath))
                            {
                                File.Delete(targetFilePath);
                            }
                        }
                        catch
                        {
                            // Ignore cleanup failures
                        }
                    }
                }
                catch (Exception ex)
                {
                    failedFiles.Add(file.FullPath);
                    writeWarning?.Invoke($"Failed to download {file.Name}: {ex.Message}");
                    
                    // Clean up failed file
                    if (File.Exists(targetFilePath))
                    {
                        try
                        {
                            File.Delete(targetFilePath);
                        }
                        catch
                        {
                            // Ignore cleanup failures
                        }
                    }
                }
            }
        }

        if (failedFiles.Count == 0)
        {
            writeSuccess?.Invoke($"Successfully downloaded {downloadedFiles} files to '{targetPath}'");
            return 0;
        }

        writeWarning?.Invoke($"Downloaded {downloadedFiles} files with {failedFiles.Count} failures:");
        foreach (var failedFile in failedFiles)
        {
            writeWarning?.Invoke($"Failed: {failedFile}");
        }
        return -1;
    }
}