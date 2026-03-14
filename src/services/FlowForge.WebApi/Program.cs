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

// Middleware
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.MapControllers();
app.MapHub<JobStatusHub>("/hubs/job-status");

app.Run();
