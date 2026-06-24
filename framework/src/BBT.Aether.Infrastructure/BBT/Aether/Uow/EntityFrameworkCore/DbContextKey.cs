using System;

namespace BBT.Aether.Uow.EntityFrameworkCore;

/// <summary>Cache key for a schema-bound DbContext instance within a UnitOfWork.</summary>
public readonly record struct DbContextKey(Type DbContextType, string Schema);
