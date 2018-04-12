#!/bin/bash

function STEP {
	echo
	echo "=> $*"
}

buildrids=("linux-x64" "win-x86" "win-x64")

dotnetdir=.

packname="NaiveSocks_dotnetcore"

pushd "$dotnetdir"

STEP build dotnet core publish...

dotnet publish -c Release -o bin/publish

for rid in "${buildrids[@]}"; do

	STEP build dotnet core publish with runtime for $rid...

	dotnet publish -c Release -o "bin/publish-$rid" -r $rid

done

popd

if getopts "u:" opt; then
	to_upload="$OPTARG"
	mkdir -p "$to_upload"
	STEP build dotnet core publish packs...
	pushd "$dotnetdir/bin"
	mkdir -p packs
	zip -r "packs/${packname}.zip" publish

	for rid in "${buildrids[@]}"; do
		tar -czvf "packs/${packname}_$rid.tar.gz" publish-$rid
		# tar --xz -cvf "packs/${packname}_$rid.tar.xz" publish-$rid
	done

	popd

	mv "$dotnetdir/bin/packs/"* "$to_upload"
fi
