using BookRent.Application.Abstractions.Persistence;
using BookRent.Application.Books;
using BookRent.Application.Loans;
using BookRent.Domain.Common;
using BookRent.Domain.Users;

namespace BookRent.Application.Users;

/// <summary>Cadastra um leitor. O e-mail e a chave natural de unicidade.</summary>
public sealed class RegisterUserHandler(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<UserResponse> HandleAsync(
        RegisterUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Valida e normaliza antes de consultar: nao faz sentido perguntar ao banco
        // por um e-mail mal formado.
        var user = User.Register(request.Name, request.Email, timeProvider.GetUtcNow());

        var created = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                // Checagem amigavel; a garantia real e o indice unico ux_users_email,
                // que o PersistenceExceptionHandler traduz caso duas criacoes corram juntas.
                if (await users.EmailExistsAsync(user.Email, ct).ConfigureAwait(false))
                {
                    throw new DomainException(
                        UserErrors.EmailAlreadyExists,
                        $"Ja existe um leitor com o e-mail {user.Email}.");
                }

                users.Add(user);

                return user;
            },
            cancellationToken).ConfigureAwait(false);

        return created.ToResponse();
    }
}

public sealed class GetUserHandler(IUserRepository users)
{
    public async Task<UserResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await users.FindReadOnlyAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(UserErrors.NotFound, $"Leitor {id} nao encontrado.");

        return user.ToResponse();
    }
}

/// <summary>
/// Historico de emprestimos do leitor, em qualquer estado. Um 404 aqui significa leitor
/// inexistente, e nao ausencia de emprestimos — por isso a existencia e checada antes.
/// </summary>
public sealed class GetUserLoansHandler(IUserRepository users, ILoanRepository loans)
{
    public async Task<PagedResult<LoanResponse>> HandleAsync(
        Guid userId,
        SearchLoansQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await users.ExistsAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(UserErrors.NotFound, $"Leitor {userId} nao encontrado.");
        }

        return await loans
            .SearchAsync((query with { UserId = userId }).Normalized(), cancellationToken)
            .ConfigureAwait(false);
    }
}

internal static class UserMappings
{
    public static UserResponse ToResponse(this User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserResponse(user.Id, user.Name, user.Email, user.CreatedAt);
    }
}
