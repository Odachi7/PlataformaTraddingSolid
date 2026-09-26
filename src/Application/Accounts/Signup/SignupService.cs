using Trading.Application.Abstractions;
using Trading.Domain.Entities;

namespace Trading.Application.Accounts.Signup;

public class SignupService
{
    private readonly IAccountRepository _accountRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SignupValidator _validator;

    public SignupService(
        IAccountRepository accountRepository,
        IPasswordHasher passwordHasher,
        SignupValidator validator)
    {
        _accountRepository = accountRepository;
        _passwordHasher = passwordHasher;
        _validator = validator;
    }

    public async Task<SignupResult> ExecutarSignup(SignupCommand command)
    {
        var validatorError = _validator.Validate(command);

        if (validatorError is not null)
            throw new ArgumentException(validatorError);


        return null;
    }
}