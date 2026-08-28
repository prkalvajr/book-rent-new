using BookRent.Domain.Users;

namespace BookRent.Application.Abstractions.Persistence;

public interface IUserRepository
{
    void Add(User user);

    Task<User?> FindReadOnlyAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>O e-mail chega ja normalizado pelo dominio — e a forma canonica do indice unico.</summary>
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default);
}
