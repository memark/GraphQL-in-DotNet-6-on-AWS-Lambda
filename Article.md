## Table of contents

1. [Introduction](#introduction)
1. [Why GraphQL](#why-graphql)
1. [Why Lambda](#why-lambda)
1. [Initial application setup](#initial-application-setup)
1. [Running locally](#running-locally)
1. [Lambda runtimes](#lambda-runtimes)
1. [Bootstrapping](#bootstrapping)
1. [Creating the Lambda package](#creating-the-lambda-package)
1. [Deploying to AWS](#deploying-to-aws)
1. [Calling the Lambda](#calling-the-lambda)
1. [Cleaning up](#cleaning-up)
1. [Bonus: Running on ARM](#bonus-running-on-arm)
1. [Summary](#summary)

## Introduction <a name="introduction"></a>

I recently set up an API for a client in my role as _Lead Cloud Architect_. .NET and AWS were givens, the remaining choices were up to me. This article is my way of writing down all the things I wish I knew when I started that work.

I assume you already know your way around [.NET 6](https://docs.microsoft.com/en-us/dotnet/core/whats-new/dotnet-6), [C# 10](https://docs.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-10), [GraphQL](https://graphql.org) and have your ~/.aws/credentials [configured](https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-files.html#cli-configure-files-methods).

## Why GraphQL <a name="why-graphql"></a>

[GraphQL](https://graphql.org) has quickly become my primary choice when it comes to building most kinds of APIs for a number of reasons:
* Great frameworks available for a variety of programming languages
* Type safety and validation for both input and output is built-in (including client-side if using codegen)
* There are different interactive "swaggers" available, only much better

Something often mentioned about GraphQL is that the client can request only whatever fields it needs. In practice I find that a less convincing argument because most of us are usually developing our API for a single client anyway.

For the .NET platform my framework of choice is [Hot Chocolate](https://chillicream.com/docs/hotchocolate). It has great docs and can generate a GraphQL schema _in runtime_ based on existing .NET types.

## Why Lambda <a name="why-lambda"></a>

Serverless is all the hype now. What attracts me most is the ease of deployment and the ability to dynamically scale based on load.

[AWS Lambda](https://aws.amazon.com/lambda/) is usually marketed (and used) as a way to run small isolated functions. Usually with 10 line Node.js examples. But it is so much more! I would argue it is the quickest and most flexible way to run any kind of API.

The only real serverless alternative on AWS is [ECS on Fargate](https://aws.amazon.com/fargate/), but that comes with a ton of configuration and also requires you to run your code in Docker.

## Initial application setup <a name="initial-application-setup"></a>

We start by creating a new dotnet project:

`dotnet new web -o MyApi && cd MyApi`

Add AspNetCore and HotChocolate:

`dotnet add package DotNetCore.AspNetCore --version "16.*"`
`dotnet add package HotChocolate.AspNetCore --version "12.*"`

Add a single GraphQL field:

```csharp
// Query.cs
using static System.Runtime.InteropServices.RuntimeInformation;

public class Query {
  public string SysInfo =>
    $"{FrameworkDescription} running on {RuntimeIdentifier}";
}
```

Set up our AspNetCore application (using the new minimal API):

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services
  .AddGraphQLServer()
  .AddQueryType<Query>();

var app = builder.Build();

app.UseRouting();

app.UseEndpoints(endpoints =>
  endpoints.MapGraphQL());

await app.RunAsync();
```

## Running locally <a name="running-locally"></a>

Let's verify that our GraphQL API works locally.

Start the API: 
`dotnet run`

Verify using [curl](https://curl.se):
`curl "http://localhost:<YourPort>/graphql?query=%7B+sysInfo+%7D"`

You should see a response similar to:
```json
{ "data": { "sysInfo":".NET 6.0.1 running on osx.12-x64" } }
```

## Lambda runtimes <a name="lambda-runtimes"></a>

AWS offers a number of different [managed runtimes](https://docs.aws.amazon.com/lambda/latest/dg/lambda-runtimes.html) for Lambda, including .NET Core, Node, Python, Ruby, Java and Go. For .NET the latest supported version is .NET Core 3.1, which I think is too old to base new applications on.

.NET 6 was released a few months ago, so that's what we'll be using. There are two main alternatives for running on a newer runtime than what AWS provides out of the box:
* Running your Lambda in Docker
* Using a custom runtime

Running your Lambda in Docker was up until recently the easiest way for custom runtimes. The Dockerfile was only two or three lines and easy to understand. But I still feel it adds a complexity that isn't always justified.

Therefore we will be using a custom runtime.

### Using a custom runtime

There is a hidden gem available from AWS, and that is the _Amazon.Lambda.AspNetCoreServer.Hosting_ [nuget package](https://www.nuget.org/packages/Amazon.Lambda.AspNetCoreServer.Hosting/). It's hardly mentioned anywhere except in a few GitHub issues, and has a whopping 425 (!) downloads as I write this. But it's in version 1.0.0 and should be stable.

Add it to the project:
`dotnet add package Amazon.Lambda.AspNetCoreServer.Hosting --version "1.*"`

Then add this:

```csharp
// Program.cs
...
builder.Services
  .AddAWSLambdaHosting(LambdaEventSource.HttpApi);
...
```

The great thing about this (except it being a one-liner!) is that if the application is not running in Lambda, that method will do nothing! So we can continue and run our API locally as before.

## Bootstrapping <a name="bootstrapping"></a>

There are two main ways of bootstrapping our Lambda function:
* Changing the assembly name to _bootstrap_
* Adding a shell script named _bootstrap_

Changing the assembly name to _bootstrap_ could be done in our `.csproj`. Although it's a seemingly harmless change, it tends to confuse developers and others when the "main" dll goes missing from the build output and an extensionless _bootstrap_ file is present instead.

Therefore my preferred way is adding a shell script named _bootstrap_:

```bash
// bootstrap
#!/bin/bash

${LAMBDA_TASK_ROOT}/MyApi
```

`LAMBDA_TASK_ROOT` is an environment variable available when the Lambda is run on AWS.

We also need to reference this file in our `.csproj` to make sure it's always published along with the rest of our code:

```xml
// MyApi.csproj
...
<ItemGroup>
  <Content Include="bootstrap">
    <CopyToOutputDirectory>Always</CopyToOutputDirectory>
  </Content>
</ItemGroup>
...
```

## Creating the Lambda package <a name="creating-the-lambda-package"></a>

We will be using the _dotnet lambda cli tool_ to package our application. (I find it has some advantages over a plain `dotnet publish` followed by `zip`.)

`dotnet new tool-manifest`
`dotnet tool install amazon.lambda.tools --version "5.*"`

I prefer to install tools like this locally. I believe global tools will eventually cause you to run into version conflicts.

We also add a default parameter to msbuild, so we don't have to specify it on the command line.

```json
// aws-lambda-tools-defaults.json
{
  "msbuild-parameters": "--self-contained true"
}
```

Building and packaging the application is done by
`dotnet lambda package -o dist/MyApi.zip`

## Deploying to AWS <a name="deploying-to-aws"></a>

The way I prefer to deploy simple Lambdas is by using the [Serverless framework](https://www.serverless.com).

(For an excellent comparison between different tools of this kind for serverless deployments on AWS, check out [this post](https://dev.to/tastefulelk/serverless-framework-vs-sam-vs-aws-cdk-1g9g) by Sebastian Bille.)

You might argue that Terraform has emerged as the de facto standard for IaC. I would tend to agree, but it comes with a cost in terms of complexity and state management. For simple setups like this, I still prefer the _Serverless_ framework.

We add some basic configuration to our `serverless.yml` file:

```yaml
// serverless.yml
service: myservice

provider:
  name: aws
  region: eu-west-2
  httpApi:
    payload: "2.0"
  lambdaHashingVersion: 20201221

functions:
  api:
    runtime: provided.al2
    package:
      artifact: dist/MyApi.zip
      individually: true
    handler: required-but-ignored
    events:
      - httpApi: "*"
```

Even though we are using AspNetCore, a Lambda is really just a function. AWS therefore requires an API Gateway in front of it. _Serverless_ takes care of this for us. The combination of _httpApi_ and _2.0_ here means that we will use the new _HTTP_ trigger of the API Gateway. This would be my preferred choice, as long as we don't need some of the [functionality](https://docs.aws.amazon.com/apigateway/latest/developerguide/http-api-vs-rest.html) still only present in the older _REST_ trigger.

_runtime: provided.al2_ means we will use the custom runtime based on Amazon Linux 2.

Now we are finally ready to deploy our Lambda!

`npx serverless@^2.70 deploy`

The output should look something like this:

```
...
endpoints:
  ANY - https://ynop5r4gx2.execute-api.eu-west-2.amazonaws.com
...
```

Here you'll find the URL where our Lambda can be reached. Let's call this &lt;YourUrl&gt;.

## Calling the Lambda <a name="calling-the-lambda"></a>

Using [curl](https://curl.se):
`curl "https://<YourUrl>/graphql?query=%7B+sysInfo+%7D"`

You should see a response similar to:
```json
{ "data": { "sysInfo":".NET 6.0.1 running on amzn.2-x64" } }
```

## Cleaning up <a name="cleaning-up"></a>

Unless you want to keep our Lambda running, you can remove all deployed AWS resources with:
`npx serverless@^2.70 remove`

[Take me to the summary!](#summary)

## Bonus: Running on ARM <a name="bonus-running-on-arm"></a>

AWS recently announced the possibility to run Lambda on the new ARM-based Graviton2 CPU. It's marketed as [faster and cheaper](https://aws.amazon.com/about-aws/whats-new/2021/09/better-price-performance-aws-lambda-functions-aws-graviton2-processor/). Note that ARM-based Lambdas are not yet available in all AWS regions and that they might not work with pre-compiled x86/x64 dependencies.

If we want to run on Graviton2 a few small changes are necessary:
* Compiling for ARM
* Configuring Lambda for ARM
* Add additional packages for ARM

### Compiling for ARM

Here we need to add our runtime target for the dotnet lambda tool to pick up:

```json
// aws-lambda-tools-defaults.json
{
  "msbuild-parameters":
    "--self-contained true --runtime linux-arm64"
}
```

### Configure Lambda for ARM

We need to specify the architecture of the Lambda function:

```yaml
// serverless.yml
functions:
  api:
    ...
    architecture: arm64
    ...
```

### Adding additional packages for ARM

According to this [GitHub issue](https://github.com/aws/aws-lambda-dotnet/issues/920) we need to add and configure an additional package when running a custom runtime on ARM:

```xml
// MyApi.csproj
...
<ItemGroup>
  <RuntimeHostConfigurationOption
    Include="System.Globalization.AppLocalIcu"
    Value="68.2.0.9"/>
  <PackageReference
    Include="Microsoft.ICU.ICU4C.Runtime"
    Version="68.2.0.9"/>
</ItemGroup>
...
```

When adding this the API stops working on non-ARM platforms though. A more portable solution is to use a condition on the `ItemGroup`, like this:

```xml
// MyApi.csproj
...
<ItemGroup Condition="'$(RuntimeIdentifier)' == 'linux-arm64'">
  <RuntimeHostConfigurationOption
    Include="System.Globalization.AppLocalIcu"
    Value="68.2.0.9"/>
  <PackageReference
    Include="Microsoft.ICU.ICU4C.Runtime"
    Version="68.2.0.9"/>
</ItemGroup>
...
```

### Building, deploying, and calling it once more

Build and deploy as before.

Call the Lambda as before.

You should see a response similar to:
```json
{ "data": { "sysInfo":".NET 6.0.1 running on amzn.2-arm64" } }
```
confirming that we are now running on ARM!

Clean up as before.

## Summary <a name="summary"></a>

That's it! We have now deployed a minimal serverless GraphQL API in .NET 6 on AWS Lambda. Full working code is available at [GitHub](https://github.com/memark/GraphQL-in-DotNet-6-on-AWS-Lambda).

Opinionated take aways:
* Use GraphQL for most APIs
* Use Hot Chocolate for GraphQL on .NET
* Use Lambda for entire APIs, not just simple functions
* Use _dotnet lambda cli tool_ for packaging
* Use _Amazon.Lambda.AspNetCoreServer.Hosting_ for custom runtimes
* Use a simple _bootstrap_ script to start the API
* Use _Serverless_ framework for deployment
* Use ARM if you can

Any comments or questions are welcome!
