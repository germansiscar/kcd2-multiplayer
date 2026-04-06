namespace KcdMp.Server.Persistence;

public class PersistenceLoadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public class PersistenceValidationException(string message)
    : Exception(message);
