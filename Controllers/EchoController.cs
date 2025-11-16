using Microsoft.AspNetCore.Mvc;
using apiAKS_NetCore.Models;

namespace apiAKS_NetCore.Controllers
{
    [ApiController]
    [Route("echo")]
    public class EchoController : ControllerBase
    {
        [HttpPost]
        public IActionResult Post([FromBody] MessageRequest req)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            var repeated = $"{req.Mensaje} {req.Mensaje}";
            return Ok(new MessageResponse(repeated));
        }
    }
}
