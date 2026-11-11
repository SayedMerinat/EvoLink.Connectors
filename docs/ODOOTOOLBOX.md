# Reproduire OdooToolbox (odootosql / sqltoodoo) avec Ortolan

Ce guide décrit comment paramétrer Ortolan pour remplacer `odootosql.exe` (Odoo → SQL Server) et `sqltoodoo.exe`
(SQL Server → Odoo). La référence est le document « OdooToolbox : comportement de référence pour la rétrocompatibilité »
(commit `716f8c1`).

Les options utilisées ici sont toutes **désactivées par défaut** : un service existant garde exactement son comportement.

| Option | Module | Rôle |
|---|---|---|
| `createTable` | `sql-synchroniser` | Crée la table si elle n'existe pas : une colonne par propriété, dans l'ordre où elles arrivent (`id` en premier pour `odoo-provider`). |
| `addMissingColumns` | `sql-synchroniser` | Ajoute les colonnes des champs ajoutés plus tard. Rien n'est jamais supprimé ni retypé. |
| `columnTypes` | `sql-synchroniser` | Type SQL imposé par colonne, ex. `{ "date_start": "DATE", "list_price": "FLOAT(24)" }`. |
| `relationFormat: "OdooToolbox"` | `odoo-provider` | Relations écrites comme odootosql : `[5, 'Nom']` (repr Python exacte), `[1, 2]`, null si vide. |
| `relationFormat: "OdooToolbox"` | `odoo-synchroniser` | Relations lues comme sqltoodoo : `[5, 'Nom']`, `[5]`, `(5,)`, `[1, 2]`, `1,2`. Un id brut reste accepté. |
| `skipNullValues` | `odoo-synchroniser` | Une valeur NULL n'est ni comparée ni écrite : elle ne vide jamais un champ Odoo. À la création, Odoo applique sa valeur par défaut. |
| `readRetries`, `readRetryDelaySeconds`, `shrinkPageOnError` | `odoo-provider`, `odoo-synchroniser` | Nouvelle tentative d'une page de lecture en échec (502, réseau, délai), avec une page deux fois plus petite à chaque fois (minimum 10). Les écritures ne sont jamais rejouées. |

## Odoo → SQL (`odootosql`)

Un service par modèle. Comme dans OdooToolbox, l'échec d'un modèle n'arrête donc pas les autres. Pour les lancer
ensemble, abonnez-les au même événement planifié.

```jsonc
{
  "name": "Odoo → SQL : product.template",
  "source": "odoo:evolink",
  "target": "sql:OdooToolbox",
  // Mirror : une ligne dont l'enregistrement a disparu d'Odoo (supprimé ou archivé) est supprimée de la table, comme
  // le faisait la recréation de la table. En Full, deleteMissing n'est pas appliqué.
  "sync": { "keyFields": ["id"], "deleteMissing": true, "defaultMode": "Mirror" },
  "stages": [
    { "name": "Export", "modules": [
      { "$type": "odoo-provider",
        "url": "https://evolink.odoo.com", "database": "evolink", "username": "ortolan", "apiKey": "@vault:odoo#ApiKey",
        "model": "product.template",
        "readFields": ["active", "name", "default_code", "sale_ok"],   // [] = tous les champs, comme fields: []
        "pageSize": 1000,                                              // page_size
        "timeZone": "UTC",                                             // datetimes en UTC, comme OdooToolbox
        "relationFormat": "OdooToolbox",                               // many2one "[5, 'Nom']"
        "readRetries": 3 },
      { "$type": "sql-synchroniser",
        "engine": "SqlServer", "connectionString": "@vault:sql.toolbox",
        "table": "my_products",                                        // sql_table_name
        "createTable": true, "addMissingColumns": true,
        "useTransaction": true }                                       // les lecteurs voient l'ancienne table jusqu'au commit
    ] }
  ]
}
```

Correspondance de la configuration :

| OdooToolbox | Ortolan |
|---|---|
| `odoo_url`, `odoo_db`, `odoo_username`, `odoo_password` | `url`, `database`, `username`, `apiKey` (de préférence `@vault:…`) |
| `sql_server`, `sql_database`, `sql_user`, `sql_password`, `sql_additional` | `connectionString`, ex. `Server=SRV\\INSTANCE;Database=OdooToolbox;Integrated Security=true;TrustServerCertificate=true` |
| `models[].name` | `model` |
| `models[].sql_table_name` | `table` |
| `models[].fields` | `readFields` (un champ inexistant est une erreur, au lieu d'être ignoré) |
| `models[].limit: 0` | `limit: 0` (défaut), pagination par `id` |
| `models[].limit: N` | `limit: N` **et** `order` = l'ordre du modèle (ex. `"priority desc, name"` pour `product.template`), **et** `"defaultMode": "Full"` (en Mirror, les lignes hors des N premiers seraient supprimées) |
| `models[].page_size` | `pageSize` |

Ce qui change par rapport à odootosql, volontairement :
- La table n'est jamais supprimée : les lignes sont créées, mises à jour ou supprimées par `id`. Si Odoo est injoignable,
  la table garde les données de la veille, et la politique de relance du service s'applique.
- Les 0 restent 0 (pas NULL), le texte est stocké en Unicode (pas de `?`), les nombres gardent leur précision.
- Les types de colonnes déduits sont `BIGINT`, `DECIMAL(38, 10)`, `BIT`, `DATETIME2`, `NVARCHAR(MAX)`. Pour retrouver
  exactement le schéma d'odootosql, imposez-les dans `columnTypes` : `INT`, `FLOAT(24)`, `MONEY`, `DATE`.
- Une ligne refusée par SQL Server n'est pas perdue en silence : l'entité échoue, l'erreur est journalisée et comptée.
- Les `json` / `properties` restent du JSON valide (pas la repr Python).

Comportements identiques : enregistrements archivés exclus (`includeArchived: false`), langue de base (pas de `language`),
datetimes en UTC (`timeZone: "UTC"`), relations au format `[5, 'Nom']`.

## SQL → Odoo (`sqltoodoo`)

```jsonc
{
  "name": "SQL → Odoo : product.template",
  "source": "sql:OdooToolbox",
  "target": "odoo:evolink",
  "sync": { "keyFields": ["default_code"], "deleteMissing": false },
  "stages": [
    { "name": "Import", "modules": [
      { "$type": "sql-provider", "engine": "SqlServer", "connectionString": "@vault:sql.toolbox",
        "query": "SELECT * FROM [product.template]" },                    // sql
      { "$type": "odoo-synchroniser",
        "url": "https://evolink.odoo.com", "database": "evolink", "username": "ortolan", "apiKey": "@vault:odoo#ApiKey",
        "model": "product.template",
        "fields": { "name": "name", "active": "active", "default_code": "default_code", "sale_ok": "sale_ok" },   // mappings
        "onlyMappedFields": true,
        "skipNullValues": true,                                         // NULL ne vide jamais un champ Odoo
        "relationFormat": "OdooToolbox",                                // many2one "[5, 'Nom']" accepté
        "timeZone": "UTC",
        "readRetries": 3 },
      // Write-back : l'identifiant Odoo (créé ou existant) est réécrit dans update_table.
      { "$type": "copy-property", "from": "meta:odoo.id", "to": "id" },
      { "$type": "sql-synchroniser", "engine": "SqlServer", "connectionString": "@vault:sql.toolbox",
        "table": "ExportCSV_03_Product_Concerto",                       // update_table, sans crochets
        "keyFields": ["default_code"],                                  // update_field
        "fields": { "default_code": "default_code", "id": "id" }, "onlyMappedFields": true }
    ] }
  ]
}
```

La différence de fond est le **rapprochement** : OdooToolbox décide « création si `id = -1`, sinon mise à jour de
l'enregistrement `id` ». Ortolan rapproche par une **clé métier** (`keyFields`, ici `default_code`) :
- un enregistrement déjà présent dans Odoo n'est jamais recréé, même si le write-back a échoué ou si la ligne SQL a
  encore `id = -1` : c'est ce qui supprime les doublons ;
- le write-back réécrit l'identifiant de **toutes** les lignes, créées ou existantes, avec des requêtes paramétrées
  (quotes, NULL, Unicode, plus de 1000 lignes et collations sans effet) ;
- en contrepartie, si la clé change dans SQL pour une ligne qui a déjà un `id`, Ortolan crée un nouvel enregistrement là où
  OdooToolbox aurait mis à jour l'ancien. Choisissez une clé stable.

Autres différences :
- Seuls les champs modifiés sont écrits : une ligne inchangée ne touche pas `write_date` et ne déclenche pas les
  automatisations Odoo (OdooToolbox réécrivait tout à chaque passage).
- Une création par enregistrement : un enregistrement invalide échoue seul, les autres continuent (OdooToolbox perdait le
  lot et tout le reste du modèle).
- Booléens : `1`, `true`, `oui` sont vrais (OdooToolbox n'acceptait que `"True"`). Vérifiez qu'aucune donnée ne comptait
  sur l'ancien `False`.
- Un champ mappé inexistant ou en lecture seule est ignoré avec un avertissement (OdooToolbox faisait échouer le modèle
  pour un champ inexistant).
- Une requête source invalide fait échouer le job avec l'erreur SQL (OdooToolbox traitait « 0 ligne » sans erreur).
- `reference` est envoyé tel quel (`res.partner,5`), au lieu de faire échouer le modèle.

## Essais sur un vrai Odoo (dev2, 11 novembre 2026)

Odoo 17.0 Enterprise (`dev2-evolink.odoo.com`), SQL Server LocalDB :

- **Export de `product.template`** (369 714 articles, 9 champs) : table créée, tout écrit en 9 min 24, aucune erreur.
  1 624 prix à 0 restés 0, relations au format `[64, 'Produits EvoLink / …']` et `[42]`, dates en UTC, noms non CP1252
  intacts, archivés exclus. **Second passage : 369 714 inchangés, 0 réécriture**, en 7 min 37.
- **Import de contacts** (`res.partner`, l'utilisateur de test n'ayant pas le droit de créer des articles) : 3 contacts
  créés, identifiants réécrits dans SQL (y compris pour la clé `ORTOLAN-TEST-O'NEIL`), Unicode intact dans Odoo, parent
  donné en `[10897, 'Ortolan Test SA']` posé, NULL qui ne vide pas le commentaire. Passages suivants : 0 création, 0
  réécriture. Contacts supprimés ensuite.
- Un utilisateur sans droit de création : chaque enregistrement échoue seul, avec le message d'Odoo, sans write-back.

Deux défauts trouvés par ces essais et corrigés :
- Une date avec fuseau (heure locale ou UTC) écrite dans une colonne `DATETIME2` paraissait modifiée à chaque passage :
  toutes les lignes étaient réécrites. Elle est maintenant comparée telle que la colonne la stocke.
- Odoo enregistre un texte écrit dans un champ html sous la forme `<p>texte</p>` : l'enregistrement paraissait modifié à
  chaque passage. `odoo-synchroniser` compare maintenant un paragraphe unique par son texte (entités et `<br>` compris).

## Exploitation

- Il n'y a pas de code de sortie : chaque job a un état (Terminé, Terminé avec erreurs, Échec), visible dans l'API, la
  GUI, le client web, et dans les journaux (fichier et SCM EvoLink).
- Les messages de log ne sont pas ceux d'evolog : une supervision qui parse `populated table … with N values` doit lire le
  rapport du job (créés, mis à jour, inchangés, supprimés, en échec).
- Le répertoire courant n'a plus d'importance : il n'y a plus de `res/evolog.exe`.
