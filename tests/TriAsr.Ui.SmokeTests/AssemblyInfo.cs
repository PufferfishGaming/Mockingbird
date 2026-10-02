// The interface language is one setting for the whole program (Loc.Instance), so tests that change it must not run beside others that read texts.
// The whole project takes a few seconds, so it simply runs one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
