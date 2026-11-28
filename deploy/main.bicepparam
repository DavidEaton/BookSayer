using './main.bicep'

param image = readEnvironmentVariable('BOOKSAYER_IMAGE')
param clientId = readEnvironmentVariable('BOOKSAYER_CLIENT_ID')
param clientSecret = readEnvironmentVariable('BOOKSAYER_CLIENT_SECRET')
