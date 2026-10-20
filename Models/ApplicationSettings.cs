public class ApplicationSettings
{
    public string AzureSql { get; set; } = string.Empty;
    public string UnprocessedFolder { get; set; } = string.Empty;
    public string ProcessedFolder { get; set; } = string.Empty;
    public string ExceptionFolder { get; set; } = string.Empty;
    public string DuplicateFolder { get; set; } = string.Empty;
    public string ConfigurationUpdateFolder { get; set; } = string.Empty;
    public string DiscardedFolder { get; set; } = string.Empty;
    public int PollingIntervalInMinutes { get; set; } = 0;
    public string EmailRecipients { get; set; } = string.Empty;
}