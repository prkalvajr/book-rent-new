namespace BookRent.Domain.Users;

/// <summary>Codigos estaveis das regras de negocio de usuario.</summary>
public static class UserErrors
{
    public const string NotFound = "user.not_found";
    public const string NameRequired = "user.name_required";
    public const string EmailInvalid = "user.email_invalid";
    public const string EmailAlreadyExists = "user.email_already_exists";
}
