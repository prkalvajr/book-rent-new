namespace BookRent.Domain.Common;

/// <summary>
/// Marca a raiz de um agregado: unidade de consistencia transacional e
/// unico ponto de entrada para alterar o grafo de objetos abaixo dela.
/// </summary>
public interface IAggregateRoot;
