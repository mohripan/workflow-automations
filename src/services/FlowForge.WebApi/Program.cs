using FlowForge.Infrastructure;
using FlowForge.WebApi.Hubs;
using FlowForge.WebApi.Middleware;
using FlowForge.WebApi.Services;
using FlowForge.WebApi.Workers;
using FlowForge.WebApi.Validators;
using FluentValidation;
using FluentValidation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add Infrastructure
builder.Services.AddInfrastructure(builder.Configuration);

// Add Services
builder.Services.AddScoped<IAutomationService, AutomationService>();
builder.Services.AddScoped<IJobService, JobService>();

// Add Controllers
builder.Services.AddControllers();

// Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "FlowForge Web API", Version = "v1" });
});

// Add FluentValidation
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateAutomationRequestValidator>();

// Add SignalR
builder.Services.AddSignalR();

// Add Background Workers
builder.Services.AddHostedService<AutomationTriggeredConsumer>();
builder.Services.AddHostedService<JobStatusChangedConsumer>();

// Add CORS
builder.Services.AddCors(options => 
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:3000"])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

var app = builder.Build();

// Programmatic Migration
using (var scope = app.Services.CreateScope())
{
    await FlowForge.Infrastructure.Persistence.DatabaseInitializer.InitializeDatabasesAsync(scope.ServiceProvider);
}

// Middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "FlowForge Web API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.MapControllers();
app.MapHub<JobStatusHub>("/hubs/job-status");

app.Run();
