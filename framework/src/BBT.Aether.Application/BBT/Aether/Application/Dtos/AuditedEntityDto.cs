using System;
using BBT.Aether.Auditing;

namespace BBT.Aether.Application.Dtos;

/// <summary>
/// This class can be inherited by DTO classes to implement <see cref="IAuditedObject"/> interface.
/// </summary>
[Serializable]
public abstract class AuditedEntityDto : CreationAuditedEntityDto, IAuditedObject
{
    /// <inheritdoc />
    public DateTime? ModifiedAt { get; set; }

    /// <inheritdoc />
    public string? ModifiedBy { get; set; }
    /// <inheritdoc />
    public string? ModifiedByBehalfOf { get; set;}
}

/// <summary>
/// This class can be inherited by DTO classes to implement <see cref="IAuditedObject"/> interface.
/// </summary>
/// <typeparam name="TPrimaryKey">Type of primary key</typeparam>
[Serializable]
public abstract class AuditedEntityDto<TPrimaryKey> : CreationAuditedEntityDto<TPrimaryKey>, IAuditedObject
{
    /// <inheritdoc />
    public DateTime? ModifiedAt { get; set; }

    /// <inheritdoc />
    public string? ModifiedBy { get; set; }
    /// <inheritdoc />
    public string? ModifiedByBehalfOf { get; set;}
}
