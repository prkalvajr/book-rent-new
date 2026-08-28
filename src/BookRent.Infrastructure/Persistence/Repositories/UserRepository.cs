using BookRent.Application.Abstractions.Persistence;
using BookRent.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace BookRent.Infrastructure.Persistence.Repositories;

internal sealed class UserRepository(BookRentDbContext dbContext) : IUserRepository
{
    public void Add(User user) => dbContext.Users.Add(user);

    public Task<User?> FindReadOnlyAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Users.AsNoTracking().AnyAsync(user => user.Id == id, cancellationToken);

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        dbContext.Users.AsNoTracking().AnyAsync(user => user.Email == email, cancellationToken);
}
