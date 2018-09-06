@echo off
echo Build and sign apk...
echo.

nuget restore "NaiveSocksAndroid\NaiveSocksAndroid.sln" || exit 10

:: msbuild /m /t:Build /p:Configuration=Release "NaiveSocksAndroid\NaiveSocksAndroid.sln" || exit 15

set KEY_PATH=NaiveSocksAndroid\NaiveSocksAndroid\key.keystore

appveyor-tools\secure-file -decrypt %KEY_PATH%.enc -secret %apksign_keystore_secret% -out %KEY_PATH% || exit 20

msbuild /t:SignAndroidPackage /p:Configuration=Release /p:AndroidKeyStore=true /p:AndroidSigningKeyAlias=hex11 /p:AndroidSigningKeyPass=%apksign_keystore_pass% /p:AndroidSigningKeyStore="%CD%\%KEY_PATH%" /p:AndroidSigningStorePass=%apksign_keystore_pass% "NaiveSocksAndroid\NaiveSocksAndroid\NaiveSocksAndroid.csproj" || exit 30

copy "NaiveSocksAndroid\NaiveSocksAndroid\bin\Release\naive.NaiveSocksAndroid-Signed.apk" "bin\upload\NaiveSocksAndroid.apk" || exit 40
