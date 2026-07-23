namespace MapRepo.NativeStore;

public sealed class DuplicateSymbolIdentityException : InvalidOperationException
{
    public DuplicateSymbolIdentityException(string symbolId, string firstFile, string secondFile)
        : base($"Symbol ID '{symbolId}' is owned by both '{firstFile}' and '{secondFile}'. Use a structural, versioned symbol identity instead of a display-string hash.")
    {
        SymbolId = symbolId;
        FirstFile = firstFile;
        SecondFile = secondFile;
    }

    public string SymbolId { get; }
    public string FirstFile { get; }
    public string SecondFile { get; }
}

public sealed class ConcurrentStoreWriteException : IOException
{
    public ConcurrentStoreWriteException(long expectedSequence, long actualSequence)
        : base($"The repository store advanced from sequence {expectedSequence} to {actualSequence} in another writer.")
    {
        ExpectedSequence = expectedSequence;
        ActualSequence = actualSequence;
    }

    public long ExpectedSequence { get; }
    public long ActualSequence { get; }
}


public sealed class DuplicateRelationshipIdentityException : InvalidOperationException
{
    public DuplicateRelationshipIdentityException(string relationshipId, string firstFile, string secondFile)
        : base($"Relationship ID '{relationshipId}' is owned by both '{firstFile}' and '{secondFile}'. Relationship IDs must include source, target, kind, and occurrence identity.")
    {
        RelationshipId = relationshipId;
        FirstFile = firstFile;
        SecondFile = secondFile;
    }

    public string RelationshipId { get; }
    public string FirstFile { get; }
    public string SecondFile { get; }
}

public sealed class ConflictingRecordIdentityException : InvalidOperationException
{
    public ConflictingRecordIdentityException(string recordKind, string recordId, string filePath)
        : base($"The same {recordKind} ID '{recordId}' describes multiple different records inside '{filePath}'.")
    {
        RecordKind = recordKind;
        RecordId = recordId;
        FilePath = filePath;
    }

    public string RecordKind { get; }
    public string RecordId { get; }
    public string FilePath { get; }
}
