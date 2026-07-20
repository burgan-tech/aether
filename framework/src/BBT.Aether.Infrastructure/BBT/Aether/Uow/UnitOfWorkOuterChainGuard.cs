using System;
using System.Collections.Generic;

namespace BBT.Aether.Uow;

internal static class UnitOfWorkOuterChainGuard
{
    public static void Validate(IUnitOfWork owner, IUnitOfWork? outer)
    {
        var visited = new HashSet<IUnitOfWork>(ReferenceEqualityComparer.Instance)
        {
            owner
        };

        for (var current = outer; current is not null; current = current.Outer)
        {
            if (!visited.Add(current))
            {
                throw new InvalidOperationException(
                    "The outer unit of work chain cannot contain a self-reference or cycle.");
            }
        }
    }
}
