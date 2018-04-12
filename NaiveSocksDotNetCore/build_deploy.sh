#!/bin/bash

function STEP {
	echo
	echo "=> $*"
}

buildrids=("linux-x64/tar.gz" "win-x86/zip" "win-x64/zip")

dotnetdir=.

packname="NaiveSocks_dotnetcore"

pushd "$dotnetdir"

STEP build dotnet core publish...

dotnet publish -c Release -o bin/publish/bin

cat > "bin/publish/run.sh" <<EOF
#!/bin/sh

dotnet bin/NaiveSocksDotNetCore.dll $*
EOF
chmod +x "bin/publish/run.sh"

cat > "bin/publish/run.bat" <<EOF
@dotnet bin/NaiveSocksDotNetCore.dll %*
EOF

for x in "${buildrids[@]}"; do
	rid=${x%/*}

	STEP build dotnet core publish with runtime for $rid...

	dotnet publish -c Release -o "bin/publish-$rid/bin" -r $rid

	case $rid in
		linux*)
			cat > "bin/publish-$rid/run.sh" <<EOF
#!/bin/sh

bin/NaiveSocksDotNetCore $*
EOF
			chmod +x "bin/publish-$rid/run.sh"
		;;
		win*)
			cat > "bin/publish-$rid/run.bat" <<EOF
@bin\NaiveSocksDotNetCore.exe %*
EOF
		;;
	esac
done

popd

if getopts "u:" opt; then
	to_upload="$OPTARG"
	mkdir -p "$to_upload"
	STEP build dotnet core publish packs...
	pushd "$dotnetdir/bin"
	mkdir -p packs
	zip -r "packs/${packname}.zip" publish

	for x in "${buildrids[@]}"; do
		rid=${x%/*}
		packtype=${x#*/}
		case $packtype in
			zip)
				zip -r "packs/${packname}_$rid.zip" publish-$rid
			;;
			tar*)
				tar -cavf "packs/${packname}_$rid.$packtype" publish-$rid
			;;
		esac
	done

	popd

	mv "$dotnetdir/bin/packs/"* "$to_upload"
fi
