var builder = WebApplication.CreateBuilder(args);

builder.Services
  .AddGraphQLServer()
  .AddQueryType<Query>();

builder.Services
  .AddAWSLambdaHosting(LambdaEventSource.HttpApi);

var app = builder.Build();

app.UseRouting();

app.UseEndpoints(endpoints =>
  endpoints.MapGraphQL());

await app.RunAsync();