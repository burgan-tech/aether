using System;
using BBT.Aether.Auditing;

namespace BBT.Aether.Application.Dtos;

/// <summary>
/// This class can be inherited by DTO classes to implement <see cref="ICreationAuditedObject"/> interface.
/// </summary>
[Serializable]
public abstract class CreationAuditedEntityDto : EntityDto, ICreationAuditedObject
{
    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public string? CreatedBy { get; set; }
    /// <inheritdoc />
    public string? CreatedByBehalfOf { get; set; }
}

/// <summary>
/// This class can be inherited by DTO classes to implement <see cref="ICreationAuditedObject"/> interface.
/// </summary>
/// <typeparam name="TPrimaryKey">Type of primary key</typeparam>
[Serializable]
public abstract class CreationAuditedEntityDto<TPrimaryKey> : EntityDto<TPrimaryKey>, ICreationAuditedObject
{
    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public string? CreatedBy { get; set; }
    /// <inheritdoc />
    public string? CreatedByBehalfOf { get; set; }
}
