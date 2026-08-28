using BookRent.Domain.Common;

namespace BookRent.Domain.Users;

/// <summary>Leitor da biblioteca. Raiz de agregado.</summary>
public sealed class User : Entity, IAggregateRoot
{
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 320;

    /// <summary>Construtor exigido pelo materializador do EF Core.</summary>
    private User()
    {
    }

    private User(string name, string email, DateTimeOffset createdAt)
        : base(Guid.CreateVersion7())
    {
        Name = name;
        Email = email;
        CreatedAt = createdAt;
    }

    public string Name { get; private set; } = null!;

    /// <summary>E-mail normalizado em minusculas. Unico entre os leitores.</summary>
    public string Email { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public static User Register(string? name, string? email, DateTimeOffset now)
    {
        var trimmedName = name?.Trim();

        if (string.IsNullOrEmpty(trimmedName))
        {
            throw new DomainException(UserErrors.NameRequired, "O nome e obrigatorio.");
        }

        if (trimmedName.Length > MaxNameLength)
        {
            throw new DomainException(UserErrors.NameRequired, $"O nome excede {MaxNameLength} caracteres.");
        }

        return new User(trimmedName, NormalizeEmail(email), now);
    }

    /// <summary>
    /// Normaliza para a forma canonica da restricao de unicidade: sem espacos nas bordas,
    /// tudo em minusculas. A validacao e estrutural — nao confirma que o endereco existe.
    /// </summary>
    public static string NormalizeEmail(string? email)
    {
        var normalized = email?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(normalized) || normalized.Length > MaxEmailLength)
        {
            throw new DomainException(UserErrors.EmailInvalid, "O e-mail e obrigatorio e deve ter ate 320 caracteres.");
        }

        var at = normalized.IndexOf('@', StringComparison.Ordinal);
        var wellFormed = at > 0
            && at == normalized.LastIndexOf('@')
            && at < normalized.Length - 1
            && normalized.IndexOf('.', at) > at + 1
            && !normalized.EndsWith('.')
            && !normalized.Contains(' ', StringComparison.Ordinal);

        if (!wellFormed)
        {
            throw new DomainException(UserErrors.EmailInvalid, "O e-mail informado nao e valido.");
        }

        return normalized;
    }
}
