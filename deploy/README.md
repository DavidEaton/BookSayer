# Host BookSayer for invited readers

The web app accepts UTF-8 Markdown and text files up to 128 KiB. Each reader gets five uploads per UTC day. A reader can close the browser after submitting a manuscript and return to **My audiobooks → Refresh status → Download MP3** later.

## Configure Microsoft Entra

1. In the Entra admin center, create **App registrations → New registration** in the Azure subscription's tenant. Choose **Accounts in this organizational directory only**. Record the application (client) ID.
2. Under **App roles**, create an enabled role with display name **BookSayer reader**, allowed member types **Users/Groups**, value **BookSayer.User**, and description **Upload and listen to personal audiobooks**.
3. Under **Enterprise applications → BookSayer → Properties**, set **Assignment required?** to **Yes**. Under **Users and groups**, assign your own account the **BookSayer reader** role.
4. In the registration's **Certificates & secrets**, create a client secret. Store its value in your secret manager, record the expiration, and supply it at deployment. Rotate it before expiry by deploying the replacement and then retiring the old credential.
5. After deployment, add the exact **redirectUri** deployment output under **App registrations → BookSayer → Authentication → Add a platform → Web**.

## Deploy

Use a dedicated Azure resource group. The deploying account needs permission to create resources and role assignments. Register the Microsoft.App, Microsoft.Storage, Microsoft.ManagedIdentity and Microsoft.KeyVault providers for your subscription. The template provisions a Container Apps environment, a web app, a scheduled job, private blob containers, managed identities, and a Key Vault key. The job starts every five minutes, including when the library is idle. Azure resources and runs can incur charges.

Build the repository's Dockerfile for Linux amd64 and publish the image to a registry the Container Apps environment can pull anonymously. For example, with Docker and an authenticated GitHub Container Registry account with package write access, run the following from the checkout. Substitute your lowercase GitHub username. Set the published package visibility to public and use its published digest for deployment. The image contains the application and FFmpeg.

```sh
image=ghcr.io/YOUR-USERNAME/booksayer
docker buildx build --platform linux/amd64 -t "$image:$(git rev-parse --short HEAD)" --push .
```

In Azure Cloud Shell (Bash), open a checkout of the same revision. The following commands deploy resources. Fill in the image digest and client ID, choose your region, and enter the client secret at the hidden prompt.

```sh
export BOOKSAYER_IMAGE='ghcr.io/YOUR-USERNAME/booksayer@sha256:YOUR-DIGEST'
export BOOKSAYER_CLIENT_ID='YOUR-APPLICATION-ID'
read -rsp 'Entra client secret: ' BOOKSAYER_CLIENT_SECRET
export BOOKSAYER_CLIENT_SECRET
echo
az group create --name booksayer --location eastus
az deployment group create --name booksayer --resource-group booksayer \
  --parameters deploy/main.bicepparam --query properties.outputs -o json
unset BOOKSAYER_CLIENT_SECRET
```

Finish registering the redirect URI, then open **webUrl**. First sign-in may need to wait for the managed identities' role assignments to propagate.

## Invite and remove readers

1. Open **Microsoft Entra → Users → New user → Invite external user**. Enter the reader's email and use the deployed web URL as their invitation redirect URL. Send the invitation.
2. Open **Enterprise applications → BookSayer → Users and groups → Add user/group**. Select the guest and assign **BookSayer reader**. Guests already in your directory only need this assignment.
3. The reader redeems the invitation in their browser using an identity method allowed by your tenant. They can subsequently open the same web URL from their iPhone or another device.
4. To revoke access, remove the assignment. Existing app cookies expire within one hour of issue. A downloaded MP3 remains in the reader's possession.

## Access and storage

The OIDC middleware validates the signed token from the configured tenant for this application's client ID. The default and fallback policies require a signed-in principal with the tenant ID, a GUID object ID and the exact reader role. Invitations and assignments are administered in Entra. The app needs no Graph directory permissions.

Razor Pages validates anti-forgery tokens on uploads. Storage paths are generated from the signed-in user's tenant and object IDs. Downloads stream through the authenticated app and check ownership on every request. A guessed ID belonging only to another reader returns 404. The application cookie's data protection key ring is shared through a private blob and wrapped using Key Vault. Only the web identity has access to the key ring.

A manuscript and its initial job status are created in one conditional blob write. Each scheduled worker scans for queued work and claims at most one book using its blob version. It renders through the existing CLI with a private temporary cache. A stopped worker's claim can be reclaimed after 50 minutes. Rendering is limited to 45 minutes per book. The app retains source and audio until an administrator removes the corresponding private blobs. Removing today's source also frees that upload slot.

## Verify locally

The HTTP integration test uses test-only identities, an Azurite blob emulator, and the real CLI and FFmpeg Flite provider. From the repository root, with .NET 10, Node.js and FFmpeg installed, start the emulator in a separate terminal:

```sh
npx --yes --package azurite azurite-blob --skipApiVersionCheck --location /tmp/booksayer-azurite
```

```sh
dotnet build -c Release
export BOOKSAYER_TEST_CLI="$PWD/src/BookSayer/bin/Release/net10.0/BookSayer.dll"
dotnet test -c Release --no-build
```

For hosted acceptance, sign in with an assigned guest, upload a short manuscript, refresh the library until it is ready and download the MP3. Open the same download URL in a separate browser session as another assigned reader with an empty library and verify 404. Check that a guest without the reader assignment cannot enter.
