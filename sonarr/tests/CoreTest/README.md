# Upstream-test fixtures

NUnit fixtures that run inside upstream's `NzbDrone.Core.Test` project (real SQLite test database,
real repositories). They are the regression tests for the review findings on patch 0003
(`DecisionScopeReviewFixture.cs`) and the re-review finding on patch 0004
(`BlocklistLikeReviewFixture.cs`: U+FFFE/U+FFFF/U+FFFD in blocklist titles).

```sh
cp -r tests/CoreTest/SonarrNext <tree>/src/NzbDrone.Core.Test/   # <tree>: build.sh's build tree
cd <tree>
dotnet build src/NzbDrone.Core.Test/Sonarr.Core.Test.csproj -c Release -p:EnableAnalyzers=false -p:TreatWarningsAsErrors=false
dotnet test _tests/net10.0/Sonarr.Core.Test.dll --filter "FullyQualifiedName~SonarrNext"
```

To check that the behavioural tests encode stock behaviour, copy them into an unpatched tree
together with `stock-stub/StockDecisionScopeStub.cs` (a `DecisionScope` that calls every loader
directly). On stock, only the three memo-mechanics tests fail (stock does not memoize):
`existing_indexer_is_read_once_per_batch_by_find_and_get`, `memo_never_stores_null` and
`unchanged_rows_are_read_once_per_batch`.
