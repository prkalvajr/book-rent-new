namespace BookRent.Domain.Books;

/// <summary>
/// Codigos estaveis das regras de negocio do catalogo. Sao contrato com o cliente:
/// aparecem na extension "code" do Problem Details e nao mudam sem versionamento.
/// </summary>
public static class BookErrors
{
    public const string NotFound = "book.not_found";
    public const string TitleRequired = "book.title_required";
    public const string AuthorRequired = "book.author_required";
    public const string IsbnInvalid = "book.isbn_invalid";
    public const string IsbnAlreadyExists = "book.isbn_already_exists";
    public const string TotalCopiesNegative = "book.total_copies_negative";
    public const string TotalBelowActiveLoans = "book.total_below_active_loans";
    public const string Inactive = "book.inactive";
    public const string AlreadyInactive = "book.already_inactive";
    public const string HasActiveLoans = "book.has_active_loans";
    public const string AvailabilityOverflow = "book.availability_overflow";
    public const string ConcurrentModification = "book.concurrent_modification";
}
