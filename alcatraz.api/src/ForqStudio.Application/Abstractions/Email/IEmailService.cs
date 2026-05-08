namespace ForqStudio.Application.Abstractions.Email;

public interface IEmailService
{
    Task SendEmailAsync(Domain.Users.Email recipient, string subject, string body);
}
