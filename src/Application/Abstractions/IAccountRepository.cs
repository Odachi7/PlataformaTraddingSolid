using Trading.Domain.Entities;

namespace Trading.Application.Abstractions;

public interface IAccountRepository
{
    Task<bool> EmailExistsAsyn(string email);

    Task AddAsync(Account account);
}