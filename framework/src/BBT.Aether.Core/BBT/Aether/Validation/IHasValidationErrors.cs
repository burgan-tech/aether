using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BBT.Aether.Validation;

public interface IHasValidationErrors
{
    IList<ValidationResult> ValidationErrors { get; }
}