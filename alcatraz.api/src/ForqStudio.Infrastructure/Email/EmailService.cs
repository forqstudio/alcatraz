using ForqStudio.Application.Abstractions.Email;
using Microsoft.Extensions.Logging;

namespace ForqStudio.Infrastructure.Email;

internal sealed class EmailService(ILogger<EmailService> logger) : IEmailService
{
    public Task SendEmailAsync(Domain.Users.Email recipient, string subject, string body)
    {
        logger.LogInformation("Sending email to {Recipient} with subject {Subject}", recipient, subject);
        return Task.CompletedTask;
    }
}
