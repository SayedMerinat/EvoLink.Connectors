# EvoLink.Odoo.Client

Client Odoo 17 par l'API externe JSON-RPC (`POST {url}/jsonrpc`, `authenticate` puis `execute_kw`). Il remplace la
librairie `EvoLink.Odoo.API` (`_Odoo`) : même protocole, API pensée pour les connecteurs, et les défauts de l'ancienne
librairie corrigés (voir en bas).

La librairie ne dépend que de Newtonsoft.Json, ni d'Ortolan ni des connecteurs : EvoBus peut l'utiliser telle quelle.
Elle est utilisée par le connecteur `EvoLink.Connectors.Odoo`.

## Démarrer

```csharp
using var odoo = new OdooJsonRpcClient("https://societe.odoo.com", "societe", "ortolan", apiKey);

// Un enregistrement par sa clé : null s'il n'existe pas.
var partner = await odoo.FindOneAsync("res.partner", OdooDomainBuilder.Equal("ref", "C001"), ["name", "parent_id", "write_date"]);

string? name = partner?.GetText("name");
long? parentId = partner?.GetId("parent_id");            // many2one [id, "nom"] → id
DateTime? modified = partner?.GetDateTimeUtc("write_date");

// Créer ou mettre à jour : crée seulement si la recherche a répondu « aucun ».
var result = await odoo.UpsertAsync("res.partner", OdooDomainBuilder.Equal("ref", "C001"),
    new Dictionary<string, object?> { ["name"] = "EvoLink SA", ["category_id"] = OdooCommands.Set([1, 2]) });
```

Une instance est utilisable en parallèle, s'authentifie une seule fois et ne partage rien avec les autres instances.
Chaque méthode accepte un `OdooContext` (langue, enregistrements archivés, fuseau, clés libres) et un `CancellationToken`.

## API

| Besoin | Méthode |
|---|---|
| Méthodes ORM (interface `IOdooClient`) | `FieldsGetAsync`, `SearchReadAsync` (une page), `SearchAsync`, `CreateAsync` (lot), `WriteAsync`, `UnlinkAsync`, `NameSearchAsync`, `ExecuteKwAsync` (toute autre méthode : `action_confirm`...) |
| Lire tout un modèle, par pages | `SearchReadAllAsync`, `FindAsync` |
| Un seul enregistrement | `FindOneAsync`, `FindIdAsync` (null si aucun, `OdooAmbiguousResultException` si plusieurs) |
| Par identifiant | `GetAsync` (`OdooRecordNotFoundException`), `ReadAsync` (ordre des identifiants conservé) |
| Compter | `CountAsync` (`search_count`) |
| Écrire | `CreateAsync` (un enregistrement), `UpdateAsync`, `UpdateWhereAsync`, `UpsertAsync` |
| Supprimer, archiver | `DeleteAsync`, `DeleteWhereAsync` (0 n'est pas une erreur), `SetActiveAsync` |
| Domaines | `OdooDomainBuilder` : `Where`, `Equal`, `In`, `Contains`, `Ids`, `And`, `Or`, `Not`, `AllOf`, `AnyOf`, `Parse` (JSON). Convertible en `JArray`. |
| Lire une valeur typée | `GetText`, `GetBool`, `GetId`, `GetReferenceName`, `GetIds`, `GetDecimal`, `GetDate`, `GetDateTimeUtc`, `GetDateTime(fuseau)` |
| Écrire une relation x2many | `OdooCommands.Set`, `Link`, `Unlink`, `Clear`, `Create` |
| Dates | `OdooFormats.Date`, `OdooFormats.DateTime` (UTC), `ToUtc` |
| Champs | `OdooFieldInfo` : type, relation, lecture seule, stocké, obligatoire, libellé, valeurs d'une sélection, `IsWritable` |

## Erreurs

| Exception | Signification | Que faire |
|---|---|---|
| `OdooUnavailableException` | Réseau, HTTP 5xx, délai dépassé, authentification ou accès refusé, réponse qui n'est pas du JSON-RPC | Réessayer plus tard (indisponibilité de la source ou de la cible) |
| `OdooConfigurationException` | Paramètres de connexion invalides, adresse qui n'est pas un Odoo (404) | Corriger la configuration |
| `OdooRpcException` | Erreur métier d'Odoo (validation, champ inconnu, droits sur un enregistrement) : `OdooMessage`, `OdooName`, `Debug` | Seul l'enregistrement traité échoue |
| `OdooRecordNotFoundException`, `OdooAmbiguousResultException` | `GetAsync` sans résultat, recherche censée désigner un seul enregistrement | Selon le cas |

La clé API n'apparaît jamais dans un message ni dans `OdooConnection.ToString()`.

## Passer d'EvoLink.Odoo.API à EvoLink.Odoo.Client

| EvoLink.Odoo.API | EvoLink.Odoo.Client |
|---|---|
| `new Odoo17Client(new JsonRpcTransport(url, db, user, key))` | `new OdooJsonRpcClient(url, db, user, key)` |
| `OdooConditionBuilder` + `OdooCondition(OdooExpression.FieldEquals(...))` | `OdooDomainBuilder.Equal(...)`, `.And(...)`, `.Or(...)` |
| `GetRecords` | `FindAsync` (toutes les pages) |
| `GetRecord` | `FindOneAsync` (null au lieu d'une exception) |
| `GetRecordId` / `GetRecordIds` | `FindIdAsync` / `FindIdsAsync` (null ou liste vide au lieu d'un échec) |
| `GetRecord(model, id)` | `GetAsync` |
| `CreateRecord` | `CreateAsync` |
| `UpdateRecord(s)` | `UpdateAsync`, `WriteAsync` (plusieurs ids en un appel), `UpdateWhereAsync` |
| `CreateOrUpdate` | `UpsertAsync` |
| `DeleteRecord(s)` | `DeleteAsync`, `DeleteWhereAsync` |
| `GetModel` / `GetFields` | `FieldsGetAsync` |
| `OdooRequestResult.IsSuccess` / `Message` | Exceptions typées (tableau ci-dessus) |
| `OdooField` / `OdooRecord` | `Dictionary<string, object?>` + `GetText`, `GetId`... ; écriture : dictionnaire de valeurs |

## Défauts de l'ancienne librairie corrigés

1. **Cache statique des modèles** (`s_models`) : un `Dictionary` non protégé, partagé par toutes les instances, indexé par le
   seul nom du modèle et jamais vidé. Il mélangeait les instances Odoo, provoquait des erreurs en parallèle et ignorait les champs
   ajoutés. Ici, aucun état partagé.
2. **Nombres à virgule convertis en `float` 32 bits** : 1 234 567.89 devenait 1 234 567.875. Ici, `double` à la lecture,
   `decimal` avec `GetDecimal`, et un `float` donné est élargi sans perte de son écriture décimale.
3. **`CreateOrUpdate` créait l'enregistrement dès que la recherche échouait**, même pour une coupure réseau (doublons).
   `UpsertAsync` ne crée que si la recherche a répondu « aucun » et refuse une recherche ambiguë.
4. **Une recherche vide était une erreur** (`GetRecordId(s)`), et `GetRecord` levait alors une `NullReferenceException`.
   Ici, rien trouvé donne null ou une liste vide.
5. **Erreurs avalées** (`catch { }`) dans la conversion des valeurs : un nom passé à un many2one partait tel quel vers Odoo.
   Ici, les conversions sont explicites (`GetId`, `OdooCommands`) et une valeur invalide lève une `FormatException`.
6. **`Call` levait « Result is empty »** pour un résultat légitime égal à la valeur par défaut du type (0, false).
7. **Pas de pagination** : `search_read` lisait tout le modèle en un appel. Ici, `SearchReadAllAsync` lit par pages.
8. **Pas de contexte, de délai réglable ni d'annulation**, un `HttpClient` créé à chaque appel quand aucun n'était fourni,
   statut HTTP non vérifié (une page de maintenance devenait une erreur de désérialisation), erreurs réseau et erreurs métier
   confondues.
9. **Dates et heures envoyées sans conversion en UTC**. Ici, `OdooFormats.DateTime` les convertit.
10. **Une écriture sur plusieurs enregistrements faisait un appel par enregistrement.** Ici, `WriteAsync` et `UnlinkAsync`
    font un seul appel, et `CreateAsync` crée un lot en un appel.
