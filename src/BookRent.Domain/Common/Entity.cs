namespace BookRent.Domain.Common;

/// <summary>
/// Base para entidades identificadas por <see cref="Guid"/>.
/// A igualdade e por identidade, nao por valor.
/// </summary>
public abstract class Entity
{
    protected Entity(Guid id) => Id = id;

    /// <summary>Construtor exigido pelos materializadores (EF Core).</summary>
    protected Entity()
    {
    }

    public Guid Id { get; protected init; }

    public override bool Equals(object? obj) =>
        obj is Entity other && other.GetType() == GetType() && other.Id == Id;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
