using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ForqStudio.Domain.Abstractions
{
    public record Error(string Code, string name)
    {
        public static Error None => new Error(string.Empty, string.Empty);
        public static Error NullValue = new("Error.NullValue", "null value was provided");
    }
}
