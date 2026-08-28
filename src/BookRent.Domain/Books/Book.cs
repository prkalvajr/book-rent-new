using BookRent.Domain.Common;
using BookRent.Domain.Loans;

namespace BookRent.Domain.Books;

/// <summary>
/// Livro do catalogo e raiz do agregado de disponibilidade.
///
/// Exemplares sao um CONTADOR (<see cref="TotalCopies"/> / <see cref="AvailableCopies"/>),
/// nao entidades individuais: nenhuma operacao do dominio precisa saber QUAL exemplar
/// foi emprestado. Ver docs/plano-implementacao.md, secao 1.1.
///
/// IMPORTANTE: <see cref="AvailableCopies"/> nunca e escrito de forma absoluta pelo
/// caminho de emprestimo. Ali a escrita e um UPDATE condicional relativo
/// (available_copies - 1 WHERE available_copies > 0), fora do change tracker.
/// Esta classe so a movimenta no ajuste administrativo de quantidade, que roda sob
/// o token de concorrencia <see cref="Version"/>.
/// </summary>
public sealed class Book : Entity, IAggregateRoot
{
    public const int MaxTitleLength = 300;
    public const int MaxAuthorLength = 200;
    public const int MaxIsbnLength = 20;

    /// <summary>Construtor exigido pelo materializador do EF Core.</summary>
    private Book()
    {
    }

    private Book(string title, string isbn, string author, int totalCopies, DateTimeOffset createdAt)
        : base(Guid.CreateVersion7())
    {
        Title = title;
        Isbn = isbn;
        Author = author;
        TotalCopies = totalCopies;
        AvailableCopies = totalCopies;
        IsActive = true;
        CreatedAt = createdAt;
        Version = 1;
    }

    // "= null!" e o padrao para entidades materializadas pelo EF: o construtor sem
    // parametros nao atribui, mas o materializador sempre preenche antes do uso.
    public string Title { get; private set; } = null!;

    /// <summary>ISBN normalizado (so digitos e X maiusculo). Unico no catalogo.</summary>
    public string Isbn { get; private set; } = null!;

    public string Author { get; private set; } = null!;

    public int TotalCopies { get; private set; }

    /// <summary>
    /// Exemplares disponiveis para emprestimo. A invariante
    /// <c>0 &lt;= AvailableCopies &lt;= TotalCopies</c> tambem e uma CHECK constraint no
    /// banco: a garantia nao depende deste codigo estar correto.
    /// </summary>
    public int AvailableCopies { get; private set; }

    /// <summary>Desativado em vez de removido: o historico de emprestimos nao pode sumir.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    /// <summary>
    /// Token de concorrencia otimista, incrementado SOMENTE pelas operacoes que escrevem
    /// campos descritivos. Emprestimo, devolucao e cancelamento mexem apenas em
    /// <see cref="AvailableCopies"/> e nao tocam aqui — do contrario todo emprestimo
    /// invalidaria a edicao de catalogo em andamento. Ver secao 9.7 do plano.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>Emprestimos ativos, derivado da invariante mantida pelo banco.</summary>
    public int ActiveLoans => TotalCopies - AvailableCopies;

    public static Book Create(string? title, string? isbn, string? author, int totalCopies, DateTimeOffset now)
    {
        if (totalCopies < 0)
        {
            throw new DomainException(
                BookErrors.TotalCopiesNegative,
                "A quantidade de exemplares nao pode ser negativa.");
        }

        return new Book(
            EnsureText(title, MaxTitleLength, BookErrors.TitleRequired, "O titulo"),
            NormalizeIsbn(isbn),
            EnsureText(author, MaxAuthorLength, BookErrors.AuthorRequired, "O autor"),
            totalCopies,
            now);
    }

    /// <summary>Altera os campos descritivos. Escrita absoluta: roda sob o token de concorrencia.</summary>
    public void UpdateDetails(string? title, string? isbn, string? author, DateTimeOffset now)
    {
        EnsureActive();

        Title = EnsureText(title, MaxTitleLength, BookErrors.TitleRequired, "O titulo");
        Isbn = NormalizeIsbn(isbn);
        Author = EnsureText(author, MaxAuthorLength, BookErrors.AuthorRequired, "O autor");

        Touch(now);
    }

    /// <summary>
    /// Ajusta o acervo para <paramref name="newTotal"/>, movendo a disponibilidade pelo
    /// mesmo delta. Reduzir abaixo dos emprestimos ativos e recusado.
    /// </summary>
    public void AdjustTotalCopies(int newTotal, DateTimeOffset now)
    {
        EnsureActive();

        if (newTotal < 0)
        {
            throw new DomainException(
                BookErrors.TotalCopiesNegative,
                "A quantidade de exemplares nao pode ser negativa.");
        }

        var delta = newTotal - TotalCopies;

        if (delta == 0)
        {
            return;
        }

        var newAvailable = AvailableCopies + delta;

        if (newAvailable < 0)
        {
            throw new DomainException(
                BookErrors.TotalBelowActiveLoans,
                $"Nao e possivel reduzir para {newTotal} exemplares: {ActiveLoans} estao emprestados.");
        }

        TotalCopies = newTotal;
        AvailableCopies = newAvailable;

        Touch(now);
    }

    /// <summary>
    /// Retira um exemplar de circulacao. Expressa no dominio a invariante que o caminho
    /// concorrente garante em SQL (<c>WHERE available_copies &gt; 0</c>) e que o banco
    /// sustenta com a CHECK constraint — a mesma regra em tres camadas.
    ///
    /// NAO incrementa <see cref="Version"/>: emprestimo nao pode invalidar uma edicao de
    /// catalogo em andamento (secao 9.7). Em producao o caminho quente nao passa por aqui,
    /// usa UPDATE condicional fora do change tracker (secao 2.1).
    /// </summary>
    public void RegisterCheckout()
    {
        EnsureActive();

        if (AvailableCopies <= 0)
        {
            throw new DomainException(
                LoanErrors.NoCopiesAvailable,
                "Nao ha exemplares disponiveis para emprestimo.");
        }

        AvailableCopies--;
    }

    /// <summary>Devolve um exemplar a circulacao. Tambem nao mexe em <see cref="Version"/>.</summary>
    public void RegisterReturn()
    {
        if (AvailableCopies >= TotalCopies)
        {
            throw new DomainException(
                BookErrors.AvailabilityOverflow,
                "Nao ha exemplar emprestado para devolver.");
        }

        AvailableCopies++;
    }

    /// <summary>Desativa o livro. Nunca apaga: o historico permanece consultavel.</summary>
    public void Deactivate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new DomainException(BookErrors.AlreadyInactive, "O livro ja esta desativado.");
        }

        IsActive = false;
        DeactivatedAt = now;

        Touch(now);
    }

    /// <summary>
    /// Normaliza o ISBN para a forma canonica usada na restricao de unicidade:
    /// sem hifens nem espacos, X maiusculo. Valida formato, nao digito verificador.
    /// </summary>
    public static string NormalizeIsbn(string? isbn)
    {
        var normalized = new string((isbn ?? string.Empty).Where(char.IsLetterOrDigit).ToArray())
            .ToUpperInvariant();

        var wellFormed = normalized.Length is 10 or 13
            && normalized.Take(normalized.Length - 1).All(char.IsAsciiDigit)
            && (char.IsAsciiDigit(normalized[^1]) || (normalized.Length == 10 && normalized[^1] == 'X'));

        if (!wellFormed)
        {
            throw new DomainException(
                BookErrors.IsbnInvalid,
                "O ISBN deve ter 10 ou 13 caracteres numericos (X aceito como ultimo digito de ISBN-10).");
        }

        return normalized;
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new DomainException(BookErrors.Inactive, "O livro esta desativado.");
        }
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private static string EnsureText(string? value, int maxLength, string errorCode, string fieldName)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException(errorCode, $"{fieldName} e obrigatorio.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainException(errorCode, $"{fieldName} excede {maxLength} caracteres.");
        }

        return trimmed;
    }
}
