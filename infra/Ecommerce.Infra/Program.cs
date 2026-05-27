using Ecommerce.Infra.Resources;
using Pulumi;

System.Diagnostics.Debugger.Launch();

var config = new Config();

return await Deployment.RunAsync<EcommerceStack>();
