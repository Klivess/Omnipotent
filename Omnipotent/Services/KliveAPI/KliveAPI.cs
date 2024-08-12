using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;


namespace Omnipotent.Services.KliveAPI
{
    public class KliveAPI : OmniService
    {
        public static int apiPORT = 80;

        public KliveAPI()
        {
            name = "KliveAPI";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain() 
        {
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions()
                {
                    ContentRootPath = AppDomain.CurrentDomain.BaseDirectory
                });

                //adds every class with the [Controller] attribute
                builder.Services.AddControllers();
                builder.Logging.ClearProviders();
                // builder.Configuration.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                //builder.Host.UseContentRoot(AppDomain.CurrentDomain.BaseDirectory);
                builder.WebHost.UseUrls($"https://*:{apiPORT}");
                builder.Logging.AddFilter("Error", LogLevel.Error);
                var app = builder.Build();
                app.UseHttpsRedirection();
                app.UseAuthorization();

                //Middleware
                app.Use(async (context, next) =>
                {
                    //context.GetEndpoint().Metadata;
                    var authorizationPassword = context.Request.Headers.Authorization;

                    //Continue
                    await next(context);
                });

                app.MapControllers();
                app.Run();
            }
            catch(Exception ex)
            {
                serviceManager.logger.LogError(name, ex, "KliveAPI Failed!");
            }
        }
    }
}