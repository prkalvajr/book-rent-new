namespace BookRent.Application.Users;

public sealed record RegisterUserRequest(string? Name, string? Email);

public sealed record UserResponse(Guid Id, string Name, string Email, DateTimeOffset CreatedAt);
