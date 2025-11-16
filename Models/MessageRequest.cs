using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace apiAKS_NetCore.Models
{
    public class MessageRequest
    {
        [Required]
        [JsonPropertyName("mensaje")]
        public string Mensaje { get; set; } = string.Empty;
    }
}
