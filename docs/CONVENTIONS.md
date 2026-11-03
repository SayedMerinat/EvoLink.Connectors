# EvoLink.Connectors – conventions

Connecteurs **natifs Ortolan** qui remplacent la logique d'EvoBus (M-Files, Odoo 17, SQL, DocSeries, transformers et
conditions). Chaque connecteur est un **plugin** chargé par le serveur Ortolan (`Ortolan:Plugins`), dans son propre
`AssemblyLoadContext`. Ortolan.Core est partagé avec l'hôte.

Dépôts côte à côte : `..\Ortolan` (lire `src/Ortolan.Core/**`, `docs/ARCHITECTURE.md`) et, **en lecture seule** pour
s'inspirer du comportement métier, `..\EvoLink.EvoBus\src\EvoLink.EvoBus.Core`. On ne modifie **jamais** EvoBus, `_Odoo`
ni Ortolan depuis ce dépôt.

## Structure

| Projet | Contenu | Alias (`$type`) |
|---|---|---|
| `EvoLink.Connectors.Common` | `ValueConverter`, `FieldMapping`, `ConnectorGuard` (bibliothèque, pas un plugin) | – |
| `EvoLink.Connectors.Sql` | MSSQL, MySQL, ODBC, OleDB | `sql-provider`, `sql-synchroniser` |
| `EvoLink.Connectors.Odoo` | Client JSON-RPC Odoo 17 natif | `odoo-provider`, `odoo-synchroniser`, `odoo-id-lookup` |
| `EvoLink.Connectors.MFiles` | API COM M-Files (`Interop.MFilesAPI`) | `mfiles-provider`, `mfiles-synchroniser`, `mfiles-user-lookup`, `mfiles-value-lookup` |
| `EvoLink.Connectors.DocSeries` | `EvoLink.DocSeries.API` | `docseries-writer` (ou `docseries-synchroniser` si l'API le permet) |
| `EvoLink.Connectors.Transformers` | Transformers et conditions EvoBus portés | voir la section dédiée |

Exception : `EvoLink.Connectors.MFiles` et ses tests ciblent `net10.0-windows` (l'interop COM `Interop.MFilesAPI`
n'existe que pour Windows).

Chaque plugin a son projet de tests `tests/<Projet>.Tests` (xUnit 2.9.3, `xunit.runner.visualstudio` 3.1.4,
`Microsoft.NET.Test.Sdk` 17.14.1, `<UsesOrtolanCore>true</UsesOrtolanCore>`, `<IsTestProject>true</IsTestProject>`).

## csproj d'un plugin

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>EvoLink.Connectors.X</RootNamespace>
    <IsOrtolanPlugin>true</IsOrtolanPlugin>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\EvoLink.Connectors.Common\EvoLink.Connectors.Common.csproj" />
  </ItemGroup>
</Project>
```

`Directory.Build.targets` ajoute la référence à Ortolan.Core (non copiée) et copie la sortie dans
`artifacts/plugins/<NomAssembly>/`.

**Compilation en parallèle** (plusieurs personnes / agents) : Ortolan.Core et Common sont précompilés, compilez donc vos
projets avec `dotnet build <projet> -p:BuildProjectReferences=false` (sauf la première fois si votre propre dépendance
n'a jamais été compilée) pour ne pas écrire en même temps dans les mêmes `obj/`.

## Règles des modules

1. **Bases Ortolan** : `ProviderModule` (lecture), `SynchroniserModule` (cible : `ReadTargetAsync`, `CreateAsync`,
   `UpdateAsync`, `DeleteAsync`, `CommitAsync`), `TransformerModule` (une entité), `ProcessorModule` (liste).
   Préférez toujours un **synchroniser** (création + mise à jour + suppression) à un simple writer.
   Les champs clés viennent de `SyncOptions.KeyFields` (surchargeables par la propriété `keyFields` du module).
2. **Alias** : `[OrtolanType("famille-role")]`, `[Description("…")]` **en français** sur la classe et chaque propriété.
3. **Propriétés de configuration** en PascalCase (sérialisées en camelCase), valeurs par défaut sensées, constructeur
   sans paramètre. Secrets : `[Sensitive]` (mot de passe, clé API, chaîne de connexion) → chiffrés, compatibles `@vault:`.
4. **Correspondance des champs** : propriété `Fields` (`Dictionary<string,string>`, propriété de l'entité → champ du
   système) et `OnlyMappedFields` (bool), via `FieldMapping`. Vide = mêmes noms.
5. **Valeurs** : conversions via `ValueConverter` (jamais `Convert.ToX` brut sur une valeur d'entité).
6. **Erreurs** :
   - configuration invalide → `ConnectorGuard.Required(...)` / `OrtolanConfigurationException` (pas de relance) ;
   - système injoignable (réseau, authentification, timeout, serveur arrêté) → `SourceUnavailableException`
     (provider) ou `TargetUnavailableException` (synchroniser / writer) pour que la politique de relance agisse ;
   - erreur propre à une entité (valeur refusée, champ inconnu) → exception normale : l'entité passe en échec, le job continue ;
   - **jamais** d'erreur avalée dans un journal à la place d'une exception, jamais de `catch` vide.
   - Un « introuvable » métier (aucun objet trouvé par une recherche) se signale par `context.LogWarning` et une
     option `failWhenNotFound` (défaut `false`).
7. **Aucun état statique mutable** (caches, compteurs) : l'état vit dans l'instance du module (une instance par job) ;
   s'il est partagé entre threads (`MaxDegreeOfParallelism` > 1), `ConcurrentDictionary` / verrous.
8. **Testabilité** : l'accès au système passe par une interface (`IOdooClient`, `IMFilesGateway`, `ISqlGateway`, …)
   avec une implémentation réelle et une **fausse** en mémoire dans les tests. Le module crée la passerelle par une
   méthode `protected virtual` surchargeable en test. Les tests ne doivent nécessiter aucun serveur.
9. **Journalisation** : `context.LogInfo/LogWarning/LogDebug`, jamais `Console`. Ne jamais journaliser un secret.
10. **Ressources** : connexions ouvertes dans `OpenAsync`/premier appel, fermées dans `CloseAsync` (synchroniser) ou
    `finally`. Reprise : `AppliesImmediately = true` si chaque opération est validée tout de suite par la cible.
11. **Code** : C# 14, nullable activé, 0 avertissement, commentaires XML en anglais, messages utilisateur en français.

## Transformers et conditions à porter (`EvoLink.Connectors.Transformers`)

Déjà présents dans Ortolan.Modules (ne pas refaire) : `rename`, `set-property`, `copy-property`, `remove-properties`,
`keep-properties`, `trim`, `filter`, `delay`, conditions `equals`, `not`, `and`, `or`, `regex`, `compare`, `has-value`.

À porter depuis EvoBus (comportement équivalent, corrigé et documenté) : `arithmetic` (ArithmeticTransformer),
`conditional` / `if-then-else` (ConditionalTransformer, IfThenElseTransformer), `composite` (CompositeTransformer),
`csv-lookup` (CsvMatchingAdditionsTransformer), `path-segment` (DirectoryExtractFromPathByIndexTransformer),
`md5-hash` (Md5HashTransformer), `file-info` (PathToFileInfoTransformer, `relativeRoot` optionnel !),
`aggregate` (PropertyAggregationTransformer), `regex-aggregate` (RegexAggregatorTransformer),
`regex-extract` (RegexSingleMatchTransformer) ; conditions `arithmetic-condition`, `date-condition`, `file-size`,
`contains` (PropertyStringContainsCondition), `property-compare` (PropertyValueComparisonCondition).
Les clés de fichier d'EvoBus (`FILE-FilePath`, …) restent identiques pour faciliter la migration, en corrigeant le bug
`LastModifiedBy` (`FILE-LastModifiedBy`).
