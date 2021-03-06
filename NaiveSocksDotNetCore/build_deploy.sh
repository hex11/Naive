#!/bin/bash

function STEP {
	echo
	echo "=> $*"
}

buildrids=()

if [[ $APPVEYOR_REPO_TAG == "true" ]]; then 
	buildrids=( "linux-x64/tar.gz" )
fi

dotnetdir=.

packname="NaiveSocks_dotnetcore"

pushd "$dotnetdir"

STEP build dotnet core publish...

dotnet build -c Release || exit 1

dotnet publish -c Release -o bin/publish/bin || exit 1

cat > "bin/publish/run.sh" << 'EOF'
#!/bin/sh
SCRIPT=$(readlink -f "$0")
exec dotnet "$(dirname "$SCRIPT")/bin/NaiveSocksDotNetCore.dll" $*
EOF
chmod +x "bin/publish/run.sh"

cat > "bin/publish/run.bat" << 'EOF'
@dotnet "%~dp0bin/NaiveSocksDotNetCore.dll" %*
EOF

cat > "bin/publish/install.sh" << 'EOF'
#!/bin/sh
SCRIPT=$(readlink -f "$0")
cd "$(dirname "$SCRIPT")"
echo Copying to /opt/nsocks ...
cp -r . /opt/nsocks/
echo Making run.sh executable ...
chmod +x /opt/nsocks/run.sh
echo Creating symbol link at /usr/bin/nsocks ...
ln -s /opt/nsocks/run.sh /usr/bin/nsocks
EOF

for x in "${buildrids[@]}"; do
	rid=${x%/*}

	STEP build dotnet core publish with runtime for $rid...

	dotnet build -c Release -o "$publishdir/bin" -r $rid || continue

	publishdir="bin/publish-$rid"

	if ! dotnet publish -c Release -o "$publishdir/bin" -r $rid ; then
		[[ -d $publishdir ]] && rm -rf "$publishdir"
		continue
	fi

	case $rid in
		linux*)
			cat > "bin/publish-$rid/run.sh" << 'EOF'
#!/bin/sh
exec "$(dirname $0)/bin/NaiveSocksDotNetCore" $*
EOF
			chmod +x "bin/publish-$rid/run.sh"
		;;
		win*)
			cat > "bin/publish-$rid/run.bat" << 'EOF'
@"%~dp0bin\NaiveSocksDotNetCore.exe" %*
EOF
		;;
	esac
done

popd

function has() {
	return hash $* 2>/dev/null
}

function pack_zip() {
	zip="$(pwd)/$1"
	dir=$2
	if has zip ; then
		(cd "$dir" ; zip -r "$zip" .) || return 1
	else
		(cd "$dir" ; 7z a "$zip" .) || return 1
	fi
}

if getopts "u:" opt; then
	to_upload="$OPTARG"
	mkdir -p "$to_upload"
	STEP build dotnet core publish packs...
	pushd "$dotnetdir/bin"
	mkdir -p packs
	pack_zip "packs/${packname}.zip" publish
	tar -cavf "packs/${packname}.tar.gz" -C publish .

	for x in "${buildrids[@]}"; do
		rid=${x%/*}
		packtype=${x#*/}
		publishdir="publish-$rid"
		[[ -d "$publishdir" ]] || continue
		case $packtype in
			zip)
				pack_zip "packs/${packname}_$rid.zip" "$publishdir"
			;;
			tar*)
				tar cavf "packs/${packname}_$rid.$packtype" -C "$publishdir" .
			;;
		esac
	done

	popd

	mv "$dotnetdir/bin/packs/"* "$to_upload"
fi
