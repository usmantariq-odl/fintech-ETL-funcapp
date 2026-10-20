using Azure;
using Azure.Communication.Email;
using System.Runtime.CompilerServices;

public static class EmailService
{
    /// <summary>
    /// Sends an email without allowing notification failures to stop site processing.
    /// </summary>
    public static void SendEmail(string subject, string message, string emailRecipients)
    {
        try
        {
            SendEmailCore(subject, message, emailRecipients);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Email notification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Isolates Azure email types so assembly-loading failures occur inside the caller's try block.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SendEmailCore(string subject, string message, string emailRecipients)
    {
        string connectionString = Environment.GetEnvironmentVariable("AutoSiteEmailConnectionString")
            ?? throw new InvalidOperationException("AutoSiteEmailConnectionString is not configured.");
        var emailClient = new EmailClient(connectionString);

        var emailMessage = new EmailMessage(
            senderAddress: Environment.GetEnvironmentVariable("AutoSiteEmailSender") ?? "alerts@octopusdtl.com",
            content: new EmailContent(subject)
            {
                PlainText = message
            },
            recipients: new EmailRecipients(emailRecipients.Split(',').Select(s => new EmailAddress(s.Trim())).ToList())
            );

        EmailSendOperation emailSendOperation = emailClient.Send(
            WaitUntil.Completed,
            emailMessage);
    }
}
