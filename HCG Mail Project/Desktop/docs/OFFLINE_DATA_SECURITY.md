# Offline copies and Outbox: protection at rest and key lifecycle (1.0.0-beta.2)

This page states what HCG Mail does with the mail it keeps on a device and what has been verified. It says where the
protection ends. It makes no claim beyond what the tests below show.

## What is stored

| Data | Where | Encrypted with |
|---|---|---|
| Offline copies: folder list, the newest page of opened folders, opened messages (not attachments) | `<data>/offline/<hash of account>/…` | the account's **offline-copy key** |
| Outbox: messages and drafts not yet handed to the server, with their attachments | `<data>/outbox/<id>.json` | the account's **Outbox key** (a different key) |

`<data>` is `%LOCALAPPDATA%\HCG\HCG Mail` on Windows and `~/Library/Application Support/HCG Mail` on macOS.

- **Encryption.** AES-256-GCM (.NET `AesGcm`) with a random 96-bit nonce per write.
- **Associated data.** The associated data binds each file to its account, its purpose and its slot (file position).
- **Readable without the key.** The Outbox envelope shows only the item id and the account id. File names are hashes, so folder names and subjects do not appear.

## Where the keys are

- Each key is a random 256-bit key, one per account and purpose. It is created on first use.
- Windows: a generic credential in **Windows Credential Manager**, `Persist = CRED_PERSIST_LOCAL_MACHINE`. Microsoft documents that this persistence is not roamed to other computers (`CRED_PERSIST_ENTERPRISE` would roam).
- macOS: a generic-password item in the user's **login Keychain** (service "HCG Mail").
- iOS: a Keychain item with `Synchronizable = false` and accessibility `WhenUnlockedThisDeviceOnly` (`IosKeychainCredentialVault`): not in iCloud Keychain, readable only while the device is unlocked, and not migrated to another device.
- Linux: no persistent store is available. Keys exist only in memory while HCG Mail runs. Copies become unreadable when it closes and are deleted. The Outbox is kept in memory for the session.
- **On disk:** keys are never written to a file by HCG Mail, and never stored next to the data they protect (tested: no key bytes, raw or base64, in any data file).
- **In memory:** while HCG Mail runs, keys it has used are held in process memory. They are not wiped from memory on exit.

## Lifecycle — what has been verified

| Step | Behaviour | Evidence |
|---|---|---|
| First copy or Outbox item | Key created in the OS store; data encrypted | `OfflineSecurityTests.Cached_mail_is_encrypted_at_rest_and_the_key_is_never_on_disk`, `Queued_messages_are_encrypted_*` |
| Real OS store | Both keys in Windows Credential Manager / macOS login Keychain, 32 bytes each, different from each other, not in any file. A new process reads them back through the store | `OfflineSecurityTests.Keys_live_only_in_the_os_store_and_are_deleted_with_the_account` (runs on the Windows CI runners; on macOS when the macOS workflow's test job runs; skipped on Linux) |
| Cross-account access | A file copied over another account's file, or to another slot, does not decrypt and is deleted. An Outbox item relabelled to another account is never loaded | `Each_account_has_its_own_key_*`, `A_file_moved_to_another_slot_*`, `An_outbox_item_relabelled_*` |
| Retention | Copies older than the chosen period are deleted at start-up, when the setting changes, and while saving. A copy read after it expired is deleted, not shown | `Retention_deletes_old_copies` |
| Storage limit | The oldest messages are deleted first, the folder list last, until the account is under its limit | `The_storage_limit_removes_the_oldest_messages_first_*` |
| Account removal | The account's copy folder, its Outbox items, and both keys are deleted from the OS store. Afterwards, a copy of a data file taken before removal no longer decrypts in HCG Mail, because the key is gone from the store | `Keys_live_only_in_the_os_store_*`, `Removing_an_account_deletes_its_copy_and_its_key_*`, `Removing_an_account_discards_only_its_outbox_and_key` |
| Offline copies turned off, or Delete offline copies | Copies and offline-copy keys deleted. Unsent Outbox mail and its key are kept | `Turning_offline_copies_off_*`, `Clearing_the_offline_copies_never_touches_the_outbox_key` |
| Removed accounts' leftovers | Folders of accounts that no longer exist are deleted at start-up | `Copies_of_accounts_that_no_longer_exist_*` |
| No upload | The offline store has no network dependency. Nothing in HCG Mail sends offline copies or the Outbox to HCG or anyone else | `The_offline_store_has_no_network_dependency`, code review |

## Limits — what deletion does not cover

- **File deletion is a normal file-system delete.** On SSDs and journaling file systems, old blocks can remain until they are reused. They contain only ciphertext: the protection rests on the key being gone, not on the blocks being erased.
- **Key deletion removes the item from the credential store.** HCG Mail cannot make guarantees about copies of the store made by others:
  - **Backups.** A backup taken earlier (Windows File History or system images, macOS Time Machine, iOS device backups) may contain both the encrypted data folder and the credential store as it was then. Restoring such a backup can make those older copies readable again.
  - The macOS login Keychain is protected by the user's login password. Windows credentials are protected by the user's Windows logon (DPAPI).
  - On iOS, items are `WhenUnlockedThisDeviceOnly`: Apple does not move them to a new device, but an encrypted backup restored onto the same iPhone can bring them back. Whether a deleted app's Keychain items are removed is iOS's decision, not HCG Mail's (checked on a device in `iOS/PHYSICAL_DEVICE_QUALIFICATION.md` P10).
- **Anyone who can sign in as the user** on the running computer can use the user's credential store, and therefore read the offline copies, just as they could read the mailbox. The encryption protects against access to the files without the user's credential store (another account, a copied disk or folder, a lost device without the login); it does not protect against the signed-in user.
- **Memory.** Keys and decrypted mail are in process memory while HCG Mail runs, and may appear in crash dumps or swap.
- **Executable Maker's Signing Vault** is unrelated: it handles Apple signing identities on the Mac that builds HCG Mail, not HCG Mail's data keys. Its encrypted private-key backup is not implemented, so signing keys exist only in that Mac's keychain and the owner's own backups.
