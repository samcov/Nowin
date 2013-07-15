Nowin
=====

Fast Owin Web Server in pure .Net 4.5

Current status is more proof of concept. But in keep-alive case with Hello World response is 3 times faster than NodeJs 0.10.7 or HttpListener.

SSL is not supported, but planned.

Other Owin .Net server samples also included. Some parts of these samples source code are modified from Katana project.

Sample: (uses Owin.Types nuget)

    var server = new Server();
    server.Start(new IPEndPoint(IPAddress.Any, 8080), env => {
        var req = new Owin.Types.OwinRequest(env);
        var resp = new Owin.Types.OwinResponse(req);
        if (req.Path == "/")
        {
            resp.StatusCode = 200;
            resp.AddHeader("Content-Type", "text/plain");
            resp.Write("Hello World!");
            return Task.Delay(0);
        }
        resp.StatusCode = 404;
        return Task.Delay(0);
    });
    Console.WriteLine("Listening on port 8080. Enter to exit.");
    Console.ReadLine();
    server.Stop();