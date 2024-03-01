using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Omnipotent.Services.KliveAPI
{
    [Route("api/test")]
    [Controller]
    public class TestController
    {
        [HttpGet]
        [Route("gettest")]
        public IActionResult GetTest()
        {
            return new OkObjectResult("Hello!");
        }
    }
}
