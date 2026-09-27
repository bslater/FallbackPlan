// Test classes run concurrently, one worker per core, and the tests inside a
// class run in order. Every test here owns its own directories, sockets and
// passphrase variable (HostHarness), so the only thing a class can share with
// another is the process. A class that must run alone says which part of the
// process it shares, beside its [DoNotParallelize].
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
