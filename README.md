# EvoLink.Connectors

Connecteurs **natifs Ortolan** qui remplacent la logique d'EvoBus : SQL, Odoo 17, M-Files, DocSeries, ainsi que les
transformers et conditions. Ils sont réécrits proprement :
- synchronisation complète (création, mise à jour, suppression) ;
- aucun état statique partagé ;
- erreurs levées au lieu d'être avalées dans les journaux ;
- secrets chiffrés ou pris dans le coffre ;
- testés sans serveur grâce à des passerelles simulées.

Chaque connecteur est un **plugin** que le serveur Ortolan charge au démarrage. Les deux dépôts restent indépendants.

| Projet | Alias |
|---|---|
| `EvoLink.Connectors.Sql` | `sql-provider`, `sql-synchroniser` |
| `EvoLink.Connectors.Odoo` | `odoo-provider`, `odoo-synchroniser`, `odoo-id-lookup` |
| `EvoLink.Connectors.MFiles` (Windows, API COM) | `mfiles-provider`, `mfiles-synchroniser`, `mfiles-user-lookup`, `mfiles-value-lookup` |
| `EvoLink.Connectors.DocSeries` | `docseries-writer` (création seule : l'API DocSeries 1.1.0 ne permet ni recherche par champ, ni mise à jour, ni suppression) |
| `EvoLink.Connectors.Transformers` | `arithmetic`, `composite`, `conditional`, `if-then-else`, `csv-lookup`, `path-segment`, `md5-hash`, `file-info`, `aggregate`, `regex-aggregate`, `regex-extract` ; conditions `arithmetic-condition`, `date-condition`, `file-size`, `contains`, `property-compare` |
| `EvoLink.Connectors.Common` | `ValueConverter`, `FieldMapping`, `ConnectorGuard` (partagés) |

Les règles de conception sont décrites dans [docs/CONVENTIONS.md](docs/CONVENTIONS.md).

## Ce qui n'est pas encore vérifié

Les 265 tests passent sur des passerelles simulées (SQLite réel pour SQL). Sur l'instance Ortolan de test, les 5 plugins
se chargent ensemble (68 types de modules) et une synchronisation SQL, une chaîne de transformers et l'échec propre d'une
connexion M-Files (classé « source inaccessible ») ont été exécutés. **Aucun appel n'a encore été fait contre un vrai
coffre M-Files, une vraie instance Odoo 17 ni un vrai serveur DocSeries** : à valider sur des environnements de test
(points précis listés dans les commentaires des passerelles `ComMFilesGateway`, `OdooJsonRpcClient`, `DocSeriesGateway`).

Différences de comportement volontaires avec EvoBus à connaître pour une migration : `md5-hash` (ordre et format des
valeurs, le hash peut différer), `file-info` (`FILE-ExcludedName`, `FILE-NumberParentFolders`, `FILE-ReadableSize`),
`file-size` (comparaison exacte). Le connecteur M-Files cible `net10.0-windows` (l'interop COM n'existe que sous Windows).

## Compiler

Les dépôts `Ortolan` et `EvoLink.Connectors` doivent être côte à côte, car les connecteurs référencent `Ortolan.Core`. Pour un autre emplacement, ajoute `/p:OrtolanRoot=...`.

```bash
dotnet build EvoLink.Connectors.slnx
```

```bash
dotnet test EvoLink.Connectors.slnx
```

Chaque connecteur est copié dans `artifacts/plugins/<Nom>/`, avec ses dépendances mais sans `Ortolan.Core`, qui est partagé avec le serveur.

## Utiliser les connecteurs dans Ortolan

Le serveur charge les plugins listés dans `Ortolan:Plugins` (fichier `appsettings.json`). Chaque entrée est un dossier organisé comme `<Nom>/<Nom>.dll`, ou le chemin direct d'une DLL.

- **En développement**, `appsettings.Development.json` pointe déjà sur `../../../EvoLink.Connectors/artifacts/plugins`. Il suffit de compiler les connecteurs puis de redémarrer le serveur.
- **En production**, copie les dossiers de `artifacts/plugins` dans le dossier `plugins` du serveur.

Au démarrage, le journal indique chaque plugin chargé ou l'erreur rencontrée. Les nouveaux modules apparaissent ensuite dans `ortolan modules` et dans la page **Modules** de la GUI.

Exemple de synchronisation vers SQL Server :

```json
{ "$type": "sql-synchroniser", "engine": "SqlServer", "connectionString": "@vault:sql.erp", "table": "dbo.Clients",
  "fields": { "id": "NoClient", "nom": "RaisonSociale" }, "softDeleteColumn": "Supprime" }
```
