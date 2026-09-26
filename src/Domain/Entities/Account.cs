namespace Trading.Domain.Entities;

public class Account
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string Email { get; private set; }
    public string Document { get; private set; }
    public string PasswordHash { get; private set; }

    public Account(Guid Id, string Name, string Email, string Document, string PasswordHash)
    {
        Id = Id;
        Name = Name;
        Email = Email;
        Document = Document;
        PasswordHash = PasswordHash;
    }
}
