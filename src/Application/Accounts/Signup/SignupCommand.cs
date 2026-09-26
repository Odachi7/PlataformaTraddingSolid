namespace Trading.Application.Accounts.Signup;

public record SignupCommand(
    string Name, 
    string Email, 
    string Document, 
    string Password
);