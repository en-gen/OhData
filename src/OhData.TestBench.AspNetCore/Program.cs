using OhData.AspNetCore;
using OhData.TestBench.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging();
builder.Services.AddOhData(ohdata =>
{
    ohdata
        .AddProfile<ParentEntitySetProfile>()
        .AddProfile<ChildEntitySetProfile>();
});

var app = builder.Build();

app.MapOhData();

app.Run();
