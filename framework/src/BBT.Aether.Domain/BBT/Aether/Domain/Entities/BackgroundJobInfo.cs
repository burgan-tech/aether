using System;
using BBT.Aether.Domain.Entities.Auditing;

namespace BBT.Aether.Domain.Entities;

public class BackgroundJobInfo : FullAuditedEntity<Guid>
{
    public required string JobName { get; set; }
}
