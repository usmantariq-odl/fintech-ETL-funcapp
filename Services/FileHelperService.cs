using Microsoft.Extensions.Options;

public sealed class FileHelperService(FintechAutoSiteConfiguration.FunctionApp.Storage.IConfigurationFiles files)
{
    public bool MoveFileToProcessed(string filePath, string FC_siteId, IOptions<ApplicationSettings> _settings)
    {
        string processedFolder = Path.Combine(_settings.Value.ProcessedFolder, FC_siteId);

        if (!files.DirectoryExists(processedFolder))
            files.CreateDirectory(processedFolder);

        string destinationPath = Path.Combine(processedFolder, Path.GetFileName(filePath));

        files.MoveFile(filePath, destinationPath);
        return true;
    }

    public bool MoveFileToConfigurationUpdate(string filePath, string FC_siteId, IOptions<ApplicationSettings> _settings)
    {
        string configurationUpdateFolder = Path.Combine(_settings.Value.ConfigurationUpdateFolder, FC_siteId);

        if (!files.DirectoryExists(configurationUpdateFolder))
            files.CreateDirectory(configurationUpdateFolder);

        string destinationPath = Path.Combine(configurationUpdateFolder, Path.GetFileName(filePath));

        files.MoveFile(filePath, destinationPath);
        return true;
    }

    public bool MoveFileToDiscarded(string filePath, string FC_siteId, IOptions<ApplicationSettings> _settings)
    {
        string discardedFolder = Path.Combine(_settings.Value.DiscardedFolder, FC_siteId);

        if (!files.DirectoryExists(discardedFolder))
            files.CreateDirectory(discardedFolder);

        string destinationPath = Path.Combine(discardedFolder, Path.GetFileName(filePath));

        files.MoveFile(filePath, destinationPath);
        return true;
    }

    public bool MoveFileToException(string filePath, string FC_siteId, IOptions<ApplicationSettings> _settings, Exception exception)
    {
        string exceptionFolder = Path.Combine(_settings.Value.ExceptionFolder, FC_siteId);

        if (!files.DirectoryExists(exceptionFolder))
            files.CreateDirectory(exceptionFolder);

        string destinationPath = Path.Combine(exceptionFolder, Path.GetFileName(filePath));

        files.MoveFile(filePath, destinationPath);

        string fileName = Path.GetFileName(filePath),
            logFile = Path.Combine(
        exceptionFolder,
        Path.GetFileNameWithoutExtension(filePath) + ".txt");

        files.AppendAllText(logFile, @$"
        Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}
        File: {fileName}

        Message:
        {exception.Message}

        Stack Trace:
        {exception.StackTrace}

        Inner Exception:
        {exception.InnerException?.Message}
        {ApplicationConstants.LOG_FILE_DIVIDER}");

        return true;
    }

    public bool MoveFileToException(string filePath, string FC_siteId, IOptions<ApplicationSettings> _settings, string exception)
    {
        string exceptionFolder = Path.Combine(_settings.Value.ExceptionFolder, FC_siteId);

        if (!files.DirectoryExists(exceptionFolder))
            files.CreateDirectory(exceptionFolder);

        string destinationPath = Path.Combine(exceptionFolder, Path.GetFileName(filePath));

        files.MoveFile(filePath, destinationPath);

        string fileName = Path.GetFileName(filePath),
            logFile = Path.Combine(
        exceptionFolder,
        Path.GetFileNameWithoutExtension(filePath) + ".txt");

        files.AppendAllText(logFile, @$"
        Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}
        File: {fileName}

        Message:
        {exception}
        {ApplicationConstants.LOG_FILE_DIVIDER}");

        return true;
    }

    public bool MoveFileToDuplicate(string filePath, string FC_siteId, IOptions<ApplicationSettings> _settings)
    {
        string duplicateFolder = Path.Combine(_settings.Value.DuplicateFolder, FC_siteId);

        if (!files.DirectoryExists(duplicateFolder))
            files.CreateDirectory(duplicateFolder);

        string destinationPath = Path.Combine(duplicateFolder, Path.GetFileName(filePath));

        files.MoveFile(filePath, destinationPath);
        return true;
    }

    public static async Task LogExceptionAsync(Exception exception)
    {
        try
        {
            string logFolder = Path.Combine(Path.GetTempPath(), "FintechAutoSiteConfiguration", "Logs");
            if (!Path.Exists(logFolder))
                Directory.CreateDirectory(logFolder);

            string logFile = Path.Combine(
                logFolder,
                $"ErrorLog_{DateTime.Now:yyyy-MM-dd}.log");

            string logEntry = $"""
        ========================================
        Date/Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
        Message   : {exception.Message}
        Exception : {exception.GetType().FullName}
        StackTrace:
        {exception.StackTrace}

        Inner Exception:
        {exception.InnerException}

        """;

            await File.AppendAllTextAsync(
                logFile,
                logEntry);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }
}
