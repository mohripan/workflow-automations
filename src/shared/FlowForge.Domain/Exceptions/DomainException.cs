namespace FlowForge.Domain.Exceptions;

public abstract class DomainException(string message) : Exception(message);

public class JobNotFoundException(Guid id) : DomainException($"Job {id} was not found.");

public class AutomationNotFoundException(Guid id) : DomainException($"Automation {id} was not found.");

public class UnknownConnectionIdException(string connectionId) : DomainException($"Unknown connection ID: {connectionId}");

public class InvalidJobTransitionException(FlowForge.Domain.Enums.JobStatus from, FlowForge.Domain.Enums.JobStatus to)
    : DomainException($"Cannot transition job from {from} to {to}");

public class InvalidAutomationException(string message) : DomainException(message);

public class InvalidTriggerConditionException(string message) : DomainException(message);

public class UnauthorizedWebhookException(Guid id) : DomainException($"Unauthorized webhook request for automation {id}");

public class HostGroupNotFoundException(Guid id) : DomainException($"Host group {id} was not found.");

public class InvalidRegistrationTokenException() : DomainException("Invalid or expired registration token.");

public class HostNotFoundException(Guid id) : DomainException($"Host {id} was not found.");
