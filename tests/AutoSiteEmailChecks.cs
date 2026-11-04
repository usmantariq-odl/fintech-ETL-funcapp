using FintechAutoSiteConfiguration.FunctionApp;
internal static class AutoSiteEmailChecks
{
    public static void Run()
    {
        var old = Environment.GetEnvironmentVariable("AutoSiteEmailConnectionString");
        try
        {
            Environment.SetEnvironmentVariable("AutoSiteEmailConnectionString", "endpoint=https://example.communication.azure.com/;accesskey=SentinelSecretKey123;");
            var notification = new AlertDigest();
            notification.Add(new EtlAlert("ConfigurationRejectedOrFailed", "002010", "/Unprocessed/CON-002010.csv",
                "Moved to /Exception/2010/", "<script>unsafe</script> SentinelSecretKey123 Password=SecretValue456", "invocation-test", DateTimeOffset.UtcNow, "ExampleDB", "example.blob.core.windows.net"));
            var message = AutoSiteErrorEmails.Render(notification, "AutoSite-test", false);
            if (!message.Subject.Contains("002010") || !message.Subject.Contains("AUTOSITE ALERT") || message.Text.Contains("SentinelSecretKey123") || message.Text.Contains("SecretValue456") || message.Html.Contains("<script>") || !message.Html.Contains("&lt;script&gt;"))
                throw new Exception("AutoSite subject, original site ID, secret redaction or HTML encoding is incorrect.");
            if (AutoSiteErrorEmails.MissingMappingMarkerName("scope/events/", "001057", "db") != AutoSiteErrorEmails.MissingMappingMarkerName("scope/events/", "1057", "db")
                || AutoSiteErrorEmails.MissingMappingMarkerName("scope/events/", "001057", "db") == AutoSiteErrorEmails.MissingMappingMarkerName("scope/events/", "001058", "db")
                || AutoSiteErrorEmails.MissingMappingMarkerName("scope/events/", "001057", "db") == AutoSiteErrorEmails.MissingMappingMarkerName("scope/events/", "001057", "otherdb"))
                throw new Exception("Per-site notification identity ignores site/database or mishandles leading zeros.");
            Console.WriteLine("PASS AutoSite error template preserves site IDs, redacts credentials and encodes diagnostics.");
        }
        finally { Environment.SetEnvironmentVariable("AutoSiteEmailConnectionString", old); }
    }
}
